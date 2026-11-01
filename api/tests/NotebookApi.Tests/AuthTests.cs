using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NotebookApi.Data;

namespace NotebookApi.Tests;

public sealed class AuthTests : IClassFixture<ApiFactory>
{
    private const string Password = TestAccounts.Password;

    private readonly ApiFactory _factory;

    public AuthTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
    }

    private static string NewEmail() => $"person-{Guid.NewGuid():N}@example.test";

    private static IReadOnlyList<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

    private static string CookieNamed(HttpResponseMessage response, string name) =>
        SetCookies(response).Single(c => c.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)).ToLowerInvariant();

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static Task<HttpResponseMessage> Register(HttpClient client, string email, string password = Password, string? inviteCode = null) =>
        client.PostAsJsonAsync("/api/auth/register", new { email, password, inviteCode });

    private static Task<HttpResponseMessage> Login(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });

    // ----------------------------------------------------------------- registering

    [Fact]
    public async Task Registering_creates_the_account_signs_the_person_in_and_sets_a_hardened_session_cookie()
    {
        var client = await _factory.AnonymousAsync();
        var email = NewEmail();

        var response = await Register(client, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());

        var session = CookieNamed(response, "notebook.auth");
        Assert.Contains("httponly", session);       // script cannot read the session
        Assert.Contains("samesite=strict", session); // it is not sent on a cross-site request
        Assert.Contains("path=/", session);
        Assert.DoesNotContain("expires=", session);  // a session cookie: gone when the browser closes
        Assert.DoesNotContain(Password, await response.Content.ReadAsStringAsync());

        await TestAccounts.RefreshTokenAsync(client);
        var me = await Json(await client.GetAsync("/api/auth/me"));
        Assert.Equal(email, me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Outside_development_every_cookie_is_marked_Secure_so_it_is_only_ever_sent_over_https()
    {
        // Every other test runs as Development, where the dev server speaks plain http and cookies cannot be Secure; this is the setting that ships.
        using var production = _factory.Production();
        var client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var me = await client.GetAsync("/api/auth/me");
        Assert.Contains("secure", CookieNamed(me, "xsrf-token"));
        Assert.Contains("secure", CookieNamed(me, "notebook.antiforgery"));
        SetToken(client, TestAccounts.TokenFrom(me));

        var registered = await Register(client, NewEmail());
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Contains("secure", CookieNamed(registered, "notebook.auth"));
    }

    [Fact]
    public async Task Outside_development_a_plain_http_request_is_refused_not_answered_with_cookies_that_could_leak()
    {
        using var production = _factory.Production();
        var client = production.CreateClient(); // http://localhost

        var me = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.InternalServerError, me.StatusCode); // the antiforgery system refuses to set a Secure cookie on an insecure request
        Assert.DoesNotContain("XSRF-TOKEN", string.Join(';', SetCookies(me)));
    }

    [Fact]
    public async Task The_password_is_stored_hashed_never_as_typed()
    {
        var user = await _factory.SignUpAsync();

        var stored = await _factory.Db(db => Task.FromResult(db.Users.Single(u => u.Id == user.Id).PasswordHash));

        Assert.NotNull(stored);
        Assert.DoesNotContain(Password, stored);
        Assert.StartsWith("AQAAAA", stored); // the ASP.NET Core Identity v3 hash format
    }

    [Fact]
    public async Task The_same_email_cannot_be_registered_twice_whatever_its_case()
    {
        var email = NewEmail();
        await _factory.SignUpAsync(email);
        var client = await _factory.AnonymousAsync();

        var again = await Register(client, email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("application/problem+json", again.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("short-11-ch")]                   // 11 characters
    [InlineData("")]
    public async Task A_password_under_twelve_characters_is_refused_with_the_reason(string password)
    {
        var client = await _factory.AnonymousAsync();

        var response = await Register(client, NewEmail(), password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("Password", out _));
    }

    [Fact]
    public async Task A_huge_password_is_refused_so_it_cannot_be_used_to_make_the_hasher_work_for_ages()
    {
        var client = await _factory.AnonymousAsync();

        var response = await Register(client, NewEmail(), new string('x', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_long_password_needs_no_digit_symbol_or_capital()
    {
        var client = await _factory.AnonymousAsync();

        var response = await Register(client, NewEmail(), "just some ordinary words");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData("@example.test")]
    public async Task An_invalid_email_is_refused(string email)
    {
        var client = await _factory.AnonymousAsync();

        var response = await Register(client, email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_over_long_email_is_refused()
    {
        var client = await _factory.AnonymousAsync();

        var response = await Register(client, new string('a', 250) + "@example.test");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task With_an_invite_code_configured_registering_needs_exactly_that_code()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Auth:InviteCode", "open-sesame-123"));
        var client = factory.CreateClient();
        await TestAccounts.RefreshTokenAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, (await Register(client, NewEmail())).StatusCode);                              // none
        Assert.Equal(HttpStatusCode.Forbidden, (await Register(client, NewEmail(), inviteCode: "open-sesame-124")).StatusCode); // close, but wrong
        Assert.Equal(HttpStatusCode.Forbidden, (await Register(client, NewEmail(), inviteCode: "OPEN-SESAME-123")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Register(client, NewEmail(), inviteCode: "open-sesame-123")).StatusCode);
    }

    [Fact]
    public async Task Without_an_invite_code_configured_a_supplied_one_is_ignored()
    {
        var client = await _factory.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Created, (await Register(client, NewEmail(), inviteCode: "whatever")).StatusCode);
    }

    // ----------------------------------------------------------------- signing in and out

    [Fact]
    public async Task Signing_in_with_the_right_password_starts_a_session()
    {
        var email = NewEmail();
        await _factory.SignUpAsync(email);
        var client = await _factory.AnonymousAsync();

        var response = await Login(client, email, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("notebook.auth=", string.Join(';', SetCookies(response)));
        await TestAccounts.RefreshTokenAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_email_get_exactly_the_same_answer()
    {
        var email = NewEmail();
        await _factory.SignUpAsync(email);
        var client = await _factory.AnonymousAsync();

        var wrongPassword = await Login(client, email, "not the right password");
        var unknownEmail = await Login(client, NewEmail(), "not the right password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        var (a, b) = (await Json(wrongPassword), await Json(unknownEmail));
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString()); // nothing tells the two apart
        Assert.Equal("Email or password is incorrect.", a.GetProperty("title").GetString());
        Assert.DoesNotContain("notebook.auth=", string.Join(';', SetCookies(wrongPassword))); // a failed sign-in starts no session
    }

    [Fact]
    public async Task After_five_wrong_passwords_the_account_is_locked_and_even_the_right_password_is_refused_for_a_while()
    {
        var email = NewEmail();
        await _factory.SignUpAsync(email);
        var client = await _factory.AnonymousAsync();

        for (var attempt = 1; attempt <= 4; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, email, $"wrong password {attempt}")).StatusCode);

        var fifth = await Login(client, email, "wrong password 5");
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.True(int.Parse(fifth.Headers.GetValues("Retry-After").Single()) > 0);

        var rightPassword = await Login(client, email, Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, rightPassword.StatusCode); // locked: guessing faster does not help, nor does being right
    }

    [Fact]
    public async Task A_locked_account_does_not_lock_anyone_else_out()
    {
        var victim = NewEmail();
        await _factory.SignUpAsync(victim);
        var other = NewEmail();
        await _factory.SignUpAsync(other);
        var client = await _factory.AnonymousAsync();
        for (var attempt = 0; attempt < 5; attempt++) await Login(client, victim, "wrong password");

        Assert.Equal(HttpStatusCode.OK, (await Login(client, other, Password)).StatusCode);
    }

    [Fact]
    public async Task A_correct_sign_in_resets_the_count_of_failures()
    {
        var email = NewEmail();
        await _factory.SignUpAsync(email);
        var client = await _factory.AnonymousAsync();

        for (var round = 0; round < 3; round++)
        {
            for (var attempt = 0; attempt < 3; attempt++) // three failures, then a success: never reaches five in a row
                Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, email, "wrong password")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Login(client, email, Password)).StatusCode);
            await TestAccounts.RefreshTokenAsync(client); // signed in now, so the page would fetch a token for that identity
        }
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_is_safe_to_repeat()
    {
        var user = await _factory.SignUpAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.PostAsync("/api/auth/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/documents")).StatusCode);
        await TestAccounts.RefreshTokenAsync(user.Client);
        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task A_session_stops_working_the_moment_the_account_is_deleted()
    {
        var user = await _factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/documents")).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromSeconds(2)); // the session is re-checked once it is older than the check interval (zero); the test clock does not move by itself

        await _factory.Db(async db => { db.Users.Remove(db.Users.Single(u => u.Id == user.Id)); await db.SaveChangesAsync(); });

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_security_stamp_signs_out_every_session_of_that_account()
    {
        var email = NewEmail();
        var first = await _factory.SignUpAsync(email);
        var second = await _factory.AnonymousAsync();
        await Login(second, email, Password);
        await TestAccounts.RefreshTokenAsync(second);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/documents")).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromSeconds(2)); // as above: let the sessions become older than the check interval

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(email))!); // what a password change does
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await first.Client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/documents")).StatusCode);
    }

    // ----------------------------------------------------------------- nothing is open by accident

    private static readonly HashSet<string> Public = ["GET /api/health", "POST /api/auth/register", "POST /api/auth/login", "POST /api/auth/logout"];

    [Fact]
    public async Task Every_route_in_the_API_contract_refuses_an_anonymous_caller_except_the_few_that_are_meant_to_be_open()
    {
        var client = await _factory.AnonymousAsync(); // holds a CSRF token, so what is tested is the authorization, not the token
        var contract = await Json(await client.GetAsync("/openapi/v1.json"));

        var checkedRoutes = 0;
        foreach (var path in contract.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var method = operation.Name.ToUpperInvariant();
                var url = path.Name.Replace("{id}", Guid.NewGuid().ToString()).Replace("{name}", "notes.md");
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });

                var response = await client.SendAsync(request);

                if (Public.Contains($"{method} {path.Name}"))
                    Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                else
                {
                    Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path.Name} answered {(int)response.StatusCode} to an anonymous caller");
                    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                }

                checkedRoutes++;
            }
        }

        Assert.True(checkedRoutes >= 12, $"only {checkedRoutes} routes were found in the contract");
    }

    [Fact]
    public async Task The_health_check_is_open_and_reveals_no_counts()
    {
        _factory.Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, """{"status":"ok"}""");
        var client = await _factory.AnonymousAsync();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok","ai":{"status":"ok"}}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_signed_out_request_is_a_401_problem_not_a_redirect_to_a_login_page()
    {
        var client = await _factory.AnonymousAsync();

        var response = await client.GetAsync("/api/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("Sign in required.", (await Json(response)).GetProperty("title").GetString());
    }

    // ----------------------------------------------------------------- CSRF

    [Fact]
    public async Task A_signed_out_page_is_given_a_token_with_its_401_so_it_can_sign_in()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var token = CookieNamed(response, "xsrf-token");
        Assert.DoesNotContain("httponly", token); // the page's script has to read this one
        Assert.Contains("samesite=strict", token);
        Assert.Contains("httponly", CookieNamed(response, "notebook.antiforgery")); // and must not be able to read the other half
    }

    [Fact]
    public async Task An_unsafe_request_without_the_token_is_refused_even_with_a_valid_session()
    {
        var user = await _factory.SignUpAsync();
        user.Client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");

        var response = await user.Client.PostAsJsonAsync("/api/ask", new { question = "Which store is used?" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(_factory.Upstream.Calls); // refused before anything was done
    }

    [Fact]
    public async Task A_token_that_was_tampered_with_is_refused()
    {
        var user = await _factory.SignUpAsync();
        var token = user.Client.DefaultRequestHeaders.GetValues("X-XSRF-TOKEN").Single();
        user.Client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        user.Client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token[..^4] + "AAAA");

        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsync("/api/documents/sync", null)).StatusCode);
    }

    [Fact]
    public async Task One_sessions_token_does_not_work_in_another_session()
    {
        var alice = await _factory.SignUpAsync();
        var mallory = await _factory.SignUpAsync();
        var aliceToken = alice.Client.DefaultRequestHeaders.GetValues("X-XSRF-TOKEN").Single();
        mallory.Client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        mallory.Client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", aliceToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await mallory.Client.PostAsync("/api/documents/sync", null)).StatusCode);
    }

    [Fact]
    public async Task A_token_issued_before_signing_in_does_not_fit_the_signed_in_person_until_the_page_asks_again()
    {
        var client = await _factory.AnonymousAsync();
        var stale = client.DefaultRequestHeaders.GetValues("X-XSRF-TOKEN").Single();
        await Register(client, NewEmail()); // signs in; the header still holds the anonymous token

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/documents/sync", null)).StatusCode);

        await TestAccounts.RefreshTokenAsync(client); // what the page does right after signing in
        Assert.NotEqual(stale, client.DefaultRequestHeaders.GetValues("X-XSRF-TOKEN").Single());
        Assert.NotEqual(HttpStatusCode.BadRequest, (await client.PostAsync("/api/documents/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Registering_and_signing_in_hand_the_page_a_token_for_the_new_identity_so_it_can_act_at_once()
    {
        var email = NewEmail();
        var registering = await _factory.AnonymousAsync();
        var registered = await Register(registering, email);
        SetToken(registering, TestAccounts.TokenFrom(registered)); // the token in the response itself: no GET /api/auth/me in between
        Assert.NotEqual(HttpStatusCode.BadRequest, (await registering.PostAsync("/api/documents/sync", null)).StatusCode);

        var signingIn = await _factory.AnonymousAsync();
        var loggedIn = await Login(signingIn, email, Password);
        SetToken(signingIn, TestAccounts.TokenFrom(loggedIn));
        Assert.NotEqual(HttpStatusCode.BadRequest, (await signingIn.PostAsync("/api/documents/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_hands_back_a_token_for_the_signed_out_page_so_signing_in_again_needs_no_reload()
    {
        var email = NewEmail();
        var user = await _factory.SignUpAsync(email); // the client's token is bound to this signed-in identity

        var loggedOut = await user.Client.PostAsync("/api/auth/logout", null);
        SetToken(user.Client, TestAccounts.TokenFrom(loggedOut));

        Assert.Equal(HttpStatusCode.OK, (await Login(user.Client, email, Password)).StatusCode); // without the fresh token this was a 400 until the page was reloaded
    }

    private static void SetToken(HttpClient client, string? token)
    {
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
    }

    [Fact]
    public async Task Signing_in_itself_needs_a_token_so_a_stranger_cannot_sign_a_victim_into_their_own_account()
    {
        var email = NewEmail();
        await _factory.SignUpAsync(email);
        var client = _factory.CreateClient(); // never asked /api/auth/me, so holds no token

        Assert.Equal(HttpStatusCode.BadRequest, (await Login(client, email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Register(client, NewEmail())).StatusCode);
    }

    [Fact]
    public async Task Reading_never_needs_a_token()
    {
        var user = await _factory.SignUpAsync();
        user.Client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");

        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/conversations")).StatusCode);
    }

    // ----------------------------------------------------------------- rate limits

    [Fact]
    public async Task Sign_in_attempts_from_one_address_are_limited_per_minute()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:AuthPerMinute", "3"));
        var client = factory.CreateClient();
        await TestAccounts.RefreshTokenAsync(client); // a GET: not limited

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, NewEmail(), "some password")).StatusCode);

        var limited = await Login(client, NewEmail(), "some password");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Registrations_share_the_same_limit_as_sign_ins()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:AuthPerMinute", "2"));
        var client = factory.CreateClient();
        await TestAccounts.RefreshTokenAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await Register(client, NewEmail())).StatusCode);
        await TestAccounts.RefreshTokenAsync(client);
        Assert.Equal(HttpStatusCode.Created, (await Register(client, NewEmail())).StatusCode);
        await TestAccounts.RefreshTokenAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Register(client, NewEmail())).StatusCode);
    }

    [Fact]
    public async Task Questions_are_limited_per_user_and_one_users_questions_do_not_use_up_anothers()
    {
        _factory.Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, """{"status":"answered","answer":"x","sources":[],"action":"retrieve","trace":[]}""");
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:AskPerMinute", "2"));
        var busy = await TestAccounts.SignUpAsync(factory);
        var quiet = await TestAccounts.SignUpAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await busy.Client.PostAsJsonAsync("/api/ask", new { question = "first question" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await busy.Client.PostAsJsonAsync("/api/ask", new { question = "second question" })).StatusCode);
        var limited = await busy.Client.PostAsJsonAsync("/api/ask", new { question = "third question" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(2, _factory.Upstream.Calls.Count); // the third never reached the model
        Assert.Equal(HttpStatusCode.OK, (await quiet.Client.PostAsJsonAsync("/api/ask", new { question = "my first question" })).StatusCode);
    }
}
