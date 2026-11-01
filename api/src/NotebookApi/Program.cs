using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotebookApi.Clients;
using NotebookApi.Data;
using NotebookApi.Infrastructure;
using NotebookApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Strict numbers: the web defaults also accept "4" for a number, and the OpenAPI document then types every integer as number | string,
// which leaks into the generated TypeScript. Callers send numbers; anything else is a 400.
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict); // what the OpenAPI generator reads
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AiServiceExceptionHandler>();
builder.Services.AddExceptionHandler<QuotaExceptionHandler>();

builder.Services.AddOptions<AiServiceOptions>()
    .Bind(builder.Configuration.GetSection(AiServiceOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ProxyOptions>()
    .Bind(builder.Configuration.GetSection(ProxyOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<QuotaOptions>()
    .Bind(builder.Configuration.GetSection(QuotaOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(ReconciliationOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<KeyStorageOptions>().Bind(builder.Configuration.GetSection(KeyStorageOptions.Section));

builder.Services.AddOptions<HttpLimitOptions>()
    .Bind(builder.Configuration.GetSection(HttpLimitOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);

// Where the database and the Data Protection keys live. Resolved from options (not read from configuration here) so a host or a test can override them.
builder.Services.AddSingleton(services =>
{
    var database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value.Path;
    if (!Path.IsPathRooted(database))
        database = Path.Combine(services.GetRequiredService<IHostEnvironment>().ContentRootPath, database);

    var keys = services.GetRequiredService<IOptions<KeyStorageOptions>>().Value.KeysPath;
    if (string.IsNullOrWhiteSpace(keys))
        keys = Path.Combine(Path.GetDirectoryName(database)!, "keys");
    else if (!Path.IsPathRooted(keys))
        keys = Path.Combine(services.GetRequiredService<IHostEnvironment>().ContentRootPath, keys);

    return new DeploymentPaths(database, keys);
});
builder.Services.AddSingleton<DocumentWriteGate>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddDbContext<NotebookDb>((services, db) =>
{
    var path = services.GetRequiredService<DeploymentPaths>().DatabasePath;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    db.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
});
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<ConversationService>();

// ----------------------------------------------------------------- behind a proxy, and what must survive a restart

// The keys that encrypt the sign-in cookie and the CSRF tokens. The default (in memory, or a profile folder that may not exist in a container)
// signs everyone out at every restart, and breaks entirely with two instances.
builder.Services.AddDataProtection().SetApplicationName("NotebookApi");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<DeploymentPaths, ILoggerFactory>((o, paths, logs) => o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(paths.KeysPath), logs));

builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<IOptions<ProxyOptions>>((o, proxy) =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownProxies.Clear(); // the defaults trust loopback; what is trusted should be exactly what was configured
        o.KnownIPNetworks.Clear();

        if (proxy.Value.Mode == ForwardedMode.TrustedProxies)
        {
            foreach (var address in proxy.Value.KnownProxies)
                o.KnownProxies.Add(System.Net.IPAddress.Parse(address));
            foreach (var network in proxy.Value.KnownNetworks)
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    });

builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});

// ----------------------------------------------------------------- who is calling

// Cookies travel over https everywhere except local development, where the dev server is plain http.
var cookieSecurity = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies(_ => { });
builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;

        // Length, not composition: twelve characters of anything beat eight with a symbol. No maximum beyond the 128 the request allows.
        o.Password.RequiredLength = 12;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;

        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<NotebookDb>()
    .AddSignInManager();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "notebook.auth";
    o.Cookie.HttpOnly = true; // script, including any injected script, cannot read the session
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = cookieSecurity;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;

    // An API answers 401 and 403 with a problem body, not a redirect to a login page.
    o.Events.OnRedirectToLogin = context => ProblemResponses.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Sign in required.");
    o.Events.OnRedirectToAccessDenied = context => ProblemResponses.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, "You do not have access to that.");
});

// The session is checked against the account on every request (the default is every 30 minutes), so deleting an account or changing its
// password signs it out immediately. It costs one indexed lookup per request.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

// Secure by default: an endpoint nobody remembered to protect still requires a signed-in user. Opting out is [AllowAnonymous], in a few named places.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = Csrf.HeaderName;
    o.Cookie.Name = "notebook.antiforgery";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = cookieSecurity;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    RateLimitPartition<string> PerMinute(string key, int limit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    // Sign-in and registration: per address, before anyone is known. (Behind a proxy this is the proxy's address until forwarded headers are configured.)
    o.AddPolicy("auth", context => PerMinute(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.AuthPerMinute));

    // Questions: per user, because each one can cost several model calls and the model quota is shared by everyone.
    o.AddPolicy("ask", context => PerMinute(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.AskPerMinute));

    o.OnRejected = (context, _) =>
    {
        TimeSpan? wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : null;
        return new ValueTask(ProblemResponses.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, "Too many requests. Try again shortly.", wait));
    };
});

// ----------------------------------------------------------------- the AI service

builder.Services.AddTransient<CorrelationIdHandler>();
builder.Services.AddTransient<AiServiceCallerHandler>();
builder.Services.AddSingleton<IndexReconciler>();
if (builder.Configuration.GetValue($"{ReconciliationOptions.Section}:{nameof(ReconciliationOptions.Enabled)}", true))
    builder.Services.AddHostedService(services => services.GetRequiredService<IndexReconciler>());

builder.Services.AddHttpClient<IAiServiceClient, AiServiceClient>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<AiServiceOptions>>().Value;
        // A trailing slash makes relative paths such as "ask" resolve under the base address instead of replacing its last segment.
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    })
    .AddHttpMessageHandler<CorrelationIdHandler>()
    .AddHttpMessageHandler<AiServiceCallerHandler>();

var app = builder.Build();

ProductionChecks.Verify(app.Services); // refuses to start outside Development with a configuration that would only fail later, for someone else

// Creates the database on first run and applies any newer migrations. Fine for one instance; several instances starting at once would need this as a separate deploy step.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<NotebookDb>().Database.Migrate();

// First, so everything after it sees the real client address and scheme. Mode None (the default) does not add it at all: nothing is believed.
var forwarding = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.Mode;
if (forwarding != ForwardedMode.None)
    app.UseForwardedHeaders(); // TrustAll leaves the trusted lists empty, which the middleware reads as "believe everyone"

if (!app.Environment.IsDevelopment())
    app.UseHsts(); // over https only: the middleware itself ignores plain http

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseSpaFiles();
app.UseRouting(); // explicitly, and after the files: static files step aside for any request routing has already matched, and the SPA fallback matches everything

app.UseAuthentication();
app.UseMiddleware<XsrfTokenIssuerMiddleware>(); // before authorization, so even the 401 for a signed-out page carries a token (see Csrf)
app.UseRateLimiter(); // after authentication, so the per-user policy knows the user
app.UseAuthorization();
app.UseMiddleware<AntiforgeryValidationMiddleware>(); // after authorization: an anonymous request to a protected route is a 401, not a token complaint

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous(); // the contract is not a secret, and the web app's types are generated from it
}

app.MapControllers();

// Unauthenticated on purpose: it is what a host's health check calls, and it says nothing but that the process answers.
app.MapGet("/healthz", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { status = "ok" });
}).AllowAnonymous().ExcludeFromDescription();

app.MapSpaFallback();

app.Run();

// Lets the test project start the real application in memory (WebApplicationFactory<Program>).
public partial class Program;
