using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotebookApi.Clients;

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

/// <summary>Starts the real application in memory, with the AI service replaced by <see cref="StubUpstream"/>.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public StubUpstream Upstream { get; } = new();
    public LogCapture Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<IAiServiceClient, AiServiceClient>().ConfigurePrimaryHttpMessageHandler(() => Upstream));
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
