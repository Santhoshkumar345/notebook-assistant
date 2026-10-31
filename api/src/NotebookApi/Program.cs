using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DocumentWriteGate>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddDbContext<NotebookDb>((services, db) =>
{
    var path = services.GetRequiredService<IOptions<DatabaseOptions>>().Value.Path;
    if (!Path.IsPathRooted(path))
        path = Path.Combine(services.GetRequiredService<IHostEnvironment>().ContentRootPath, path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    db.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
});
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<ConversationService>();

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

// Creates the database on first run and applies any newer migrations. Fine for one instance; several instances starting at once would need this as a separate deploy step.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<NotebookDb>().Database.Migrate();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();

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

app.Run();

// Lets the test project start the real application in memory (WebApplicationFactory<Program>).
public partial class Program;
