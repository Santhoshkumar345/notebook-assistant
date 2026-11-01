using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotebookApi.Infrastructure;

namespace NotebookApi.Tests;

/// <summary>Everything that makes the application fit to put on a server: what the browser is told, what is served, who is believed, what survives a restart.</summary>
public sealed class HostingTests : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notebook-web-" + Guid.NewGuid().ToString("N"));
    private string WebRoot => Path.Combine(_root, "browser");

    public HostingTests(ApiFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));
        File.WriteAllText(Path.Combine(WebRoot, "index.html"), "<!doctype html><title>the app</title><script src=\"main-ABCD1234.js\"></script>");
        File.WriteAllText(Path.Combine(WebRoot, "main-ABCD1234.js"), "console.log('app');");
        File.WriteAllText(Path.Combine(WebRoot, "styles.css"), "body{}");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "not for the web");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private WebApplicationFactory<Program> WithSpa(Action<IWebHostBuilder>? more = null) =>
        _factory.WithWebHostBuilder(b => { b.UseWebRoot(WebRoot); more?.Invoke(b); });

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ----------------------------------------------------------------- what the browser is told

    public static TheoryData<string> Addresses => new()
    {
        "/healthz",             // an anonymous success
        "/api/documents",       // a refused request (401), written by the cookie handler
        "/api/does-not-exist",  // the JSON 404
        "/",                    // the app itself
        "/main-ABCD1234.js",    // a static file
    };

    [Theory]
    [MemberData(nameof(Addresses))]
    public async Task Every_response_carries_the_hardening_headers(string address)
    {
        using var spa = WithSpa();
        var response = await spa.CreateClient().GetAsync(address);

        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
    }

    [Fact]
    public async Task The_policy_allows_no_script_but_the_apps_own_and_no_framing()
    {
        var csp = SecurityHeadersMiddleware.ContentSecurityPolicy;

        Assert.Contains("script-src 'self';", csp); // no 'unsafe-inline', no 'unsafe-eval', no other origin
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("base-uri 'self'", csp);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Everything_under_api_is_marked_no_store_but_the_apps_files_are_not()
    {
        using var spa = WithSpa();
        var client = spa.CreateClient();

        Assert.Equal("no-store", (await client.GetAsync("/api/documents")).Headers.CacheControl?.ToString());
        Assert.Equal("no-store", (await client.GetAsync("/api/does-not-exist")).Headers.CacheControl?.ToString());
        Assert.NotEqual("no-store", (await client.GetAsync("/styles.css")).Headers.CacheControl?.ToString());
    }

    // ----------------------------------------------------------------- serving the app

    [Fact]
    public async Task The_root_and_the_apps_own_routes_get_the_app_without_signing_in_and_it_is_never_cached()
    {
        using var spa = WithSpa();
        var client = spa.CreateClient();

        foreach (var address in new[] { "/", "/chat", "/chat/6f1c2a3e-0000-0000-0000-000000000000", "/documents" })
        {
            var response = await client.GetAsync(address);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<title>the app</title>", await response.Content.ReadAsStringAsync());
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString()); // it names the current scripts, so always ask
        }
    }

    [Fact]
    public async Task Content_named_files_are_cached_for_good_and_others_are_revalidated()
    {
        using var spa = WithSpa();
        var client = spa.CreateClient();

        var hashed = await client.GetAsync("/main-ABCD1234.js");
        var plain = await client.GetAsync("/styles.css");

        Assert.Equal(HttpStatusCode.OK, hashed.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", hashed.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", plain.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_missing_file_is_a_404_not_the_app_so_a_broken_script_link_fails_loudly()
    {
        using var spa = WithSpa();

        var response = await spa.CreateClient().GetAsync("/main-GONE0000.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_api_address_is_a_JSON_404_never_the_app_page()
    {
        using var spa = WithSpa();

        var response = await spa.CreateClient().GetAsync("/api/nothing/here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_a_GET_is_answered_with_the_app()
    {
        using var spa = WithSpa();

        var client = spa.CreateClient();
        await TestAccounts.RefreshTokenAsync(client); // with a valid token, so the request gets past the CSRF check and meets the fallback itself

        var response = await client.PostAsync("/chat", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_real_API_route_wins_over_the_fallback()
    {
        using var spa = WithSpa();
        var client = spa.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // the API's own answer to a stranger, not the app page
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_file_outside_the_web_root_cannot_be_reached_by_climbing_out_of_it()
    {
        using var spa = WithSpa();
        var client = spa.CreateClient();

        foreach (var address in new[] { "/../secret.txt", "/%2e%2e/secret.txt", "/..%2fsecret.txt", "/%2e%2e%2fsecret.txt", "/assets/../../secret.txt" })
        {
            var response = await client.GetAsync(address);
            Assert.DoesNotContain("not for the web", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Without_a_web_build_only_the_API_answers_which_is_how_development_works()
    {
        var response = await _factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ----------------------------------------------------------------- health

    [Fact]
    public async Task The_health_check_answers_without_signing_in_says_nothing_else_and_is_never_cached()
    {
        var response = await _factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_health_check_does_not_call_the_AI_service()
    {
        _factory.Upstream.Reset();

        await _factory.CreateClient().GetAsync("/healthz");

        Assert.Empty(_factory.Upstream.Calls); // a liveness probe must not fail, or restart the API, because something else is down
    }

    // ----------------------------------------------------------------- body size

    [Fact]
    public async Task A_body_over_the_limit_is_refused_with_413_before_anything_reads_it_or_calls_upstream()
    {
        using var small = _factory.WithWebHostBuilder(b => b.UseSetting("Limits:MaxRequestBodyBytes", "2048"));
        var user = await TestAccounts.SignUpAsync(small);
        _factory.Upstream.Reset();

        var response = await user.Client.PostAsJsonAsync("/api/documents", new { name = "big.md", content = new string('x', 5000) });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(_factory.Upstream.Calls);
    }

    [Fact]
    public async Task A_body_at_the_limit_is_accepted()
    {
        using var small = _factory.WithWebHostBuilder(b => b.UseSetting("Limits:MaxRequestBodyBytes", "2048"));
        var user = await TestAccounts.SignUpAsync(small);
        _factory.Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":1}""");

        var response = await user.Client.PostAsJsonAsync("/api/documents", new { name = "ok.md", content = new string('x', 500) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task The_default_limit_still_allows_a_largest_allowed_document_in_the_worst_case_of_escaping()
    {
        // 200,000 characters, every one of which a JSON encoder writes as six: " for a quote, for instance.
        var worstCase = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { name = "a.md", content = new string('"', 200_000) }));

        Assert.True(worstCase < new HttpLimitOptions().MaxRequestBodyBytes, $"worst case {worstCase} bytes");
    }

    // ----------------------------------------------------------------- who is believed

    private static async Task<HttpResponseMessage> Get(HttpClient client, string address, string? forwardedFor = null, string? proto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (proto is not null) request.Headers.Add("X-Forwarded-Proto", proto);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task With_forwarding_off_a_claimed_https_is_not_believed_so_plain_http_is_still_refused_outside_development()
    {
        using var production = _factory.Production();

        var response = await Get(production.CreateClient(), "/api/auth/me", proto: "https");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); // the fail-closed behaviour, not a way round it
    }

    [Fact]
    public async Task Trusting_every_proxy_makes_a_forwarded_https_request_work_and_the_cookies_Secure()
    {
        using var production = _factory.Production(b => b.UseSetting("ForwardedHeaders:Mode", "TrustAll"));

        var response = await Get(production.CreateClient(), "/api/auth/me", proto: "https");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // nobody is signed in: that is the normal answer, not the 500 of "plain http"
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(HttpStatusCode First, HttpStatusCode Second)> TwoSignInAttempts(WebApplicationFactory<Program> app, string? firstFrom, string? secondFrom)
    {
        var client = app.CreateClient();
        await TestAccounts.RefreshTokenAsync(client);

        async Task<HttpStatusCode> Attempt(string? from)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "nobody@example.test", password = "wrong wrong wrong" }) };
            if (from is not null) request.Headers.Add("X-Forwarded-For", from);
            return (await client.SendAsync(request)).StatusCode;
        }

        return (await Attempt(firstFrom), await Attempt(secondFrom));
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_sign_in_limit_is_per_visitor_not_per_proxy()
    {
        using var app = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ForwardedHeaders:Mode", "TrustAll");
            b.UseSetting("RateLimits:AuthPerMinute", "1");
        });

        var (first, secondFromAnother) = await TwoSignInAttempts(app, "203.0.113.1", "203.0.113.2");
        var (again, secondFromSame) = await TwoSignInAttempts(app, "203.0.113.3", "203.0.113.3");

        Assert.Equal(HttpStatusCode.Unauthorized, first);
        Assert.Equal(HttpStatusCode.Unauthorized, secondFromAnother); // a different visitor has their own allowance
        Assert.Equal(HttpStatusCode.Unauthorized, again);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondFromSame); // the same visitor does not
    }

    [Fact]
    public async Task With_forwarding_off_a_claimed_address_buys_no_extra_attempts()
    {
        using var app = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:AuthPerMinute", "1"));

        var (_, second) = await TwoSignInAttempts(app, "203.0.113.1", "203.0.113.2"); // each "address" is claimed, not real

        Assert.Equal(HttpStatusCode.TooManyRequests, second);
    }

    /// <summary>Makes the connection appear to come from this address, as a real proxy's would.</summary>
    private static void ConnectingFrom(IWebHostBuilder builder, string address) =>
        builder.ConfigureServices(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new FromAddress(IPAddress.Parse(address))));

    private sealed class FromAddress(IPAddress address) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task Only_the_listed_proxies_are_believed()
    {
        using var listed = _factory.Production(b =>
        {
            b.UseSetting("ForwardedHeaders:Mode", "TrustedProxies");
            b.UseSetting("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8");
            ConnectingFrom(b, "10.1.2.3");
        });
        using var unlisted = _factory.Production(b =>
        {
            b.UseSetting("ForwardedHeaders:Mode", "TrustedProxies");
            b.UseSetting("ForwardedHeaders:KnownNetworks:0", "192.168.0.0/16");
            ConnectingFrom(b, "10.1.2.3");
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(listed.CreateClient(), "/api/auth/me", proto: "https")).StatusCode); // 401 = served normally to a stranger; 500 would be "plain http"
        Assert.Equal(HttpStatusCode.InternalServerError, (await Get(unlisted.CreateClient(), "/api/auth/me", proto: "https")).StatusCode); // anyone else could say the same
    }

    [Fact]
    public async Task A_single_listed_proxy_address_is_believed_and_loopback_is_not_trusted_by_default()
    {
        using var single = _factory.Production(b =>
        {
            b.UseSetting("ForwardedHeaders:Mode", "TrustedProxies");
            b.UseSetting("ForwardedHeaders:KnownProxies:0", "10.9.9.9");
            ConnectingFrom(b, "10.9.9.9");
        });
        using var loopback = _factory.Production(b =>
        {
            b.UseSetting("ForwardedHeaders:Mode", "TrustedProxies");
            b.UseSetting("ForwardedHeaders:KnownProxies:0", "10.9.9.9");
            ConnectingFrom(b, "127.0.0.1"); // ASP.NET's own default would trust this; configuration says only 10.9.9.9
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(single.CreateClient(), "/api/auth/me", proto: "https")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await Get(loopback.CreateClient(), "/api/auth/me", proto: "https")).StatusCode);
    }

    [Theory]
    [InlineData("TrustedProxies", null, "TrustedProxies needs at least one entry")]
    [InlineData("TrustedProxies", "not-an-address", "is not an IP address")]
    public void A_proxy_configuration_that_cannot_work_stops_the_app_from_starting(string mode, string? proxy, string expected)
    {
        using var app = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ForwardedHeaders:Mode", mode);
            if (proxy is not null) b.UseSetting("ForwardedHeaders:KnownProxies:0", proxy);
        });

        var failure = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains(expected, failure.ToString());
    }

    [Fact]
    public void A_network_that_is_not_in_CIDR_form_stops_the_app_from_starting()
    {
        using var app = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ForwardedHeaders:Mode", "TrustedProxies");
            b.UseSetting("ForwardedHeaders:KnownNetworks:0", "10.0.0.0");
        });

        Assert.Contains("CIDR", Assert.ThrowsAny<Exception>(() => app.CreateClient()).ToString());
    }

    // ----------------------------------------------------------------- HSTS

    private static readonly Uri Https = new("https://app.example.test");

    private static void NoExcludedHosts(IWebHostBuilder b) =>
        b.ConfigureServices(services => services.Configure<HstsOptions>(o => o.ExcludedHosts.Clear())); // localhost-like names are excluded by default

    [Fact]
    public async Task Outside_development_https_responses_tell_the_browser_to_keep_using_https()
    {
        using var production = _factory.Production(NoExcludedHosts);

        var response = await production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Https }).GetAsync("/healthz");

        var hsts = response.Headers.GetValues("Strict-Transport-Security").Single();
        Assert.Contains("max-age=31536000", hsts);
        Assert.Contains("includeSubDomains", hsts);
    }

    [Fact]
    public async Task In_development_there_is_no_HSTS_because_the_dev_server_is_plain_http_and_a_browser_would_remember_it()
    {
        using var development = _factory.WithWebHostBuilder(b => { NoExcludedHosts(b); b.UseEnvironment(Environments.Development); });

        var response = await development.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Https }).GetAsync("/healthz");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Plain_http_never_gets_an_HSTS_header()
    {
        using var production = _factory.Production(NoExcludedHosts);

        var response = await production.CreateClient().GetAsync("/healthz"); // http

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    // ----------------------------------------------------------------- keys that survive a restart

    [Fact]
    public async Task The_sign_in_keys_are_kept_in_the_configured_folder_so_a_restart_does_not_sign_everyone_out()
    {
        using var before = new ApiFactory();
        _ = before.CreateClient();
        var protectedByFirst = before.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("a session");
        Assert.NotEmpty(Directory.GetFiles(before.KeysPath, "key-*.xml")); // written to disk, not held in memory

        using var restarted = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("DataProtection:KeysPath", before.KeysPath));
        _ = restarted.CreateClient();
        var readBack = restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedByFirst);

        Assert.Equal("a session", readBack);
        await Task.CompletedTask;
    }

    [Fact]
    public void Different_key_folders_do_not_read_each_others_data_so_the_folder_really_is_what_matters()
    {
        using var one = new ApiFactory();
        using var other = new ApiFactory();
        _ = one.CreateClient();
        _ = other.CreateClient();
        var protectedByOne = one.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("a session");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            other.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedByOne));
    }

    [Fact]
    public void Without_a_configured_folder_the_keys_go_next_to_the_database()
    {
        var paths = _factory.Services.GetRequiredService<DeploymentPaths>();
        Assert.Equal(_factory.KeysPath, paths.KeysPath); // the test factory configures one; check the default separately:

        using var defaulted = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("DataProtection:KeysPath", "");
            b.UseSetting("Database:Path", Path.Combine(_root, "data", "notebook.db"));
        });
        var defaultPaths = defaulted.Services.GetRequiredService<DeploymentPaths>();

        Assert.Equal(Path.Combine(_root, "data", "keys"), defaultPaths.KeysPath);
    }

    // ----------------------------------------------------------------- refusing to start half-configured

    private static string StartFailure(WebApplicationFactory<Program> app) => Assert.ThrowsAny<Exception>(() => app.CreateClient()).ToString();

    [Fact]
    public void Outside_development_it_will_not_start_with_registration_open_by_accident()
    {
        using var production = _factory.WithWebHostBuilder(b => b.UseEnvironment(Environments.Production));

        var failure = StartFailure(production);

        Assert.Contains("Auth:InviteCode", failure);
        Assert.Contains("Auth:OpenRegistration", failure);
    }

    [Fact]
    public void An_invite_code_is_enough_to_start()
    {
        using var production = _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting("Auth:InviteCode", "an invite code");
        });

        _ = production.CreateClient(); // does not throw
    }

    [Fact]
    public void Development_needs_neither()
    {
        _ = _factory.WithWebHostBuilder(b => b.UseEnvironment(Environments.Development)).CreateClient();
    }

    [Fact]
    public void It_will_not_start_when_it_cannot_write_where_it_must_keep_things()
    {
        var aFile = Path.Combine(_root, "plain-file");
        File.WriteAllText(aFile, "x");
        using var production = _factory.Production(b => b.UseSetting("DataProtection:KeysPath", Path.Combine(aFile, "keys"))); // a folder cannot be made inside a file

        var failure = StartFailure(production);

        Assert.Contains("is not writable", failure);
        Assert.Contains("keys", failure);
    }

    [Fact]
    public void A_missing_but_creatable_folder_is_fine_it_is_created()
    {
        var keys = Path.Combine(_root, "not", "yet", "there");
        using var production = _factory.Production(b => b.UseSetting("DataProtection:KeysPath", keys));

        _ = production.CreateClient();

        Assert.True(Directory.Exists(keys));
    }

    private List<string> WarningsAbout(Func<WebApplicationFactory<Program>> start)
    {
        var before = _factory.Logs.Entries.Count;
        using var app = start();
        _ = app.CreateClient();
        return _factory.Logs.Entries.Skip(before).Where(e => e.Category == "ProductionChecks").Select(e => e.Message).ToList();
    }

    [Fact]
    public void A_proxy_setting_of_None_outside_development_is_warned_about_not_forbidden()
    {
        var warnings = WarningsAbout(() => _factory.Production());

        Assert.Contains(warnings, w => w.Contains("ForwardedHeaders:Mode is None"));
    }

    [Fact]
    public void No_proxy_warning_once_a_mode_is_chosen()
    {
        var warnings = WarningsAbout(() => _factory.Production(b => b.UseSetting("ForwardedHeaders:Mode", "TrustAll")));

        Assert.DoesNotContain(warnings, w => w.Contains("ForwardedHeaders"));
    }

    [Fact]
    public void An_AI_service_reached_over_plain_http_across_a_network_is_warned_about_but_loopback_is_not()
    {
        var remote = WarningsAbout(() => _factory.Production(b => b.UseSetting("AiService:BaseUrl", "http://ai.internal:8000")));
        var local = WarningsAbout(() => _factory.Production(b => b.UseSetting("AiService:BaseUrl", "http://127.0.0.1:8000")));
        var secure = WarningsAbout(() => _factory.Production(b => b.UseSetting("AiService:BaseUrl", "https://ai.example.test")));

        Assert.Contains(remote, w => w.Contains("plain http to ai.internal"));
        Assert.DoesNotContain(local, w => w.Contains("AiService:BaseUrl"));
        Assert.DoesNotContain(secure, w => w.Contains("AiService:BaseUrl"));
    }
}
