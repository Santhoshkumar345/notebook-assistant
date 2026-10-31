using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using NotebookApi.Clients;
using NotebookApi.Contracts;
using NotebookApi.Data;
using NotebookApi.Infrastructure;

namespace NotebookApi.Tests;

public sealed record Captured(HttpMethod Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Stands in for the Python AI service: records every request and answers with whatever the test configured.</summary>
public sealed class StubUpstream : HttpMessageHandler
{
    private readonly List<Captured> _calls = [];

    public IReadOnlyList<Captured> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.OK, "{}");

    public void Reset()
    {
        lock (_calls) _calls.Clear();
        Respond = _ => Json(HttpStatusCode.OK, "{}");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        lock (_calls) _calls.Add(new Captured(request.Method, request.RequestUri!.PathAndQuery, headers, body));
        return Respond(request);
    }
}

/// <summary>A clock the test moves by hand, so ordering and timestamps are exact instead of depending on how fast the test runs.</summary>
public sealed class ManualClock : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = Start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Starts the real application in memory, with the AI service replaced by <see cref="StubUpstream"/> and the database by an
/// in-memory SQLite one. The real migrations are applied to it at startup, so the tests exercise the schema that ships.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ServiceKey = "test-service-key-0123456789abcdef";

    // An in-memory SQLite database lives only as long as its connection, so one connection is opened and shared by every scope.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public StubUpstream Upstream { get; } = new();
    public LogCapture Logs { get; } = new();
    public ManualClock Clock { get; } = new();

    public ApiFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The API refuses to start without a service key. Rate limits are lifted for ordinary tests (every test signs people up and in
        // from one address); the tests about the limits lower them again with WithWebHostBuilder.
        builder.UseSetting("AiService:ApiKey", ServiceKey);
        builder.UseSetting("RateLimits:AuthPerMinute", "100000");
        builder.UseSetting("RateLimits:AskPerMinute", "100000");

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<IAiServiceClient, AiServiceClient>().ConfigurePrimaryHttpMessageHandler(() => Upstream);

            // EF Core keeps the AddDbContext lambda as a separate registration; both must go or the file-based SQLite setup would be applied again on top.
            services.RemoveAll<DbContextOptions<NotebookDb>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NotebookDb>>();
            services.AddDbContext<NotebookDb>(options => options.UseSqlite(_connection));

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>The database as nobody sees it: the query filters match no row. Use IgnoreQueryFilters to look at everything.</summary>
    public async Task<T> Db<T>(Func<NotebookDb, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<NotebookDb>());
    }

    public Task Db(Func<NotebookDb, Task> work) => Db(async db => { await work(db); return 0; });

    /// <summary>The database as one user sees it, exactly as the API would show it to them.</summary>
    public async Task<T> DbFor<T>(Guid? user, Func<NotebookDb, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        using var db = new NotebookDb(scope.ServiceProvider.GetRequiredService<DbContextOptions<NotebookDb>>(), new FixedUser(user));
        return await work(db);
    }

    public Task DbFor(Guid? user, Func<NotebookDb, Task> work) => DbFor(user, async db => { await work(db); return 0; });

    /// <summary>Empties the database (every user's rows, and the users) and the stub, for tests that share this factory.</summary>
    public async Task ResetAsync()
    {
        Upstream.Reset();
        await Db(async db =>
        {
            await db.Messages.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Conversations.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Documents.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Users.ExecuteDeleteAsync();
        });
    }

    /// <summary>Registers a new user with a client of their own (its own cookie jar and CSRF token), signed in.</summary>
    public Task<TestUser> SignUpAsync(string? email = null, string? password = null) => TestAccounts.SignUpAsync(this, email, password);

    public TestUser SignUp(string? email = null, string? password = null) => SignUpAsync(email, password).GetAwaiter().GetResult();

    /// <summary>A client nobody is signed in on, already holding a CSRF token (as the page would after its first call).</summary>
    public async Task<HttpClient> AnonymousAsync()
    {
        var client = CreateClient();
        await TestAccounts.RefreshTokenAsync(client);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

/// <summary>A signed-in test user: who they are, and a client that is signed in as them and sends the CSRF token the way Angular does.</summary>
public sealed record TestUser(Guid Id, string Email, HttpClient Client)
{
    /// <summary>How the AI service names this user: their id as 32 lowercase hex characters.</summary>
    public string Tenant => Id.ToString("N");
}

public sealed class FixedUser(Guid? id) : ICurrentUser
{
    public Guid? Id { get; } = id;
}

public static class TestAccounts
{
    // A test value for accounts that exist only inside a test run's in-memory database.
    public const string Password = "correct horse battery staple";

    public static async Task<TestUser> SignUpAsync(WebApplicationFactory<Program> factory, string? email = null, string? password = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@example.test";
        var client = factory.CreateClient();
        await RefreshTokenAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password = password ?? Password });
        response.EnsureSuccessStatusCode();
        var user = (await response.Content.ReadFromJsonAsync<UserInfo>())!;

        await RefreshTokenAsync(client); // the token is bound to who is signed in, so the one from before registering no longer fits
        return new TestUser(user.Id, email, client);
    }

    /// <summary>What Angular does on load and after signing in: GET /api/auth/me, keep the XSRF-TOKEN cookie's value, send it as X-XSRF-TOKEN on every unsafe request.</summary>
    public static async Task RefreshTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/me");
        var token = TokenFrom(response) ?? throw new InvalidOperationException("GET /api/auth/me did not issue an XSRF-TOKEN cookie.");
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
    }

    public static string? TokenFrom(HttpResponseMessage response)
    {
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)) : null;
        return cookie is null ? null : Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }
}

public sealed record LogEntry(string Category, string Message, IReadOnlyDictionary<string, object> Scope);

/// <summary>Records log entries together with the scope values active when each was written.</summary>
public sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly List<LogEntry> _entries = [];
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose() { }

    private sealed class CaptureLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object>();
            owner._scopes.ForEachScope((value, bag) =>
            {
                if (value is IEnumerable<KeyValuePair<string, object>> pairs)
                    foreach (var pair in pairs) bag[pair.Key] = pair.Value;
            }, scope);
            lock (owner._entries) owner._entries.Add(new LogEntry(category, formatter(state, exception), scope));
        }
    }
}
