using Microsoft.Extensions.Options;

namespace NotebookApi.Infrastructure;

/// <summary>Where the app keeps what must survive a restart: the database and the Data Protection keys.</summary>
public sealed record DeploymentPaths(string DatabasePath, string KeysPath);

/// <summary>
/// Outside Development, refuses to start with a configuration that would only show its problem later and to someone else: registration open to the
/// whole internet by accident, or a folder the app cannot write to (found by the first person to sign in, not by whoever deployed it).
/// Things that are risky but legitimate are logged as warnings instead.
/// </summary>
public static class ProductionChecks
{
    public static void Verify(IServiceProvider services)
    {
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment())
            return;

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ProductionChecks");
        var problems = new List<string>();

        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (string.IsNullOrEmpty(auth.InviteCode) && !auth.OpenRegistration)
            problems.Add("Registration would be open to anyone who finds the address, which hands them your model quota. Set Auth:InviteCode, or set Auth:OpenRegistration to true to say that open registration is intended.");

        var paths = services.GetRequiredService<DeploymentPaths>();
        foreach (var (what, folder) in new[] { ("the database folder", Path.GetDirectoryName(paths.DatabasePath)!), ("the Data Protection keys folder", paths.KeysPath) })
        {
            if (TryWrite(folder) is { } reason)
                problems.Add($"{what} '{folder}' is not writable ({reason}). Mount a writable volume there, or point Database:Path / DataProtection:KeysPath elsewhere.");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("This configuration is not complete enough to run outside Development:\n - " + string.Join("\n - ", problems));

        if (services.GetRequiredService<IOptions<ProxyOptions>>().Value.Mode == ForwardedMode.None)
            logger.LogWarning("ForwardedHeaders:Mode is None. Behind a proxy that ends TLS the API would see plain http (cookies refused, per-address limits shared by everyone); set it to TrustedProxies or TrustAll.");

        var upstream = new Uri(services.GetRequiredService<IOptions<AiServiceOptions>>().Value.BaseUrl);
        if (upstream.Scheme == Uri.UriSchemeHttp && !upstream.IsLoopback)
            logger.LogWarning("AiService:BaseUrl is plain http to {Host}: the service key and every document cross that network unencrypted. Keep it on a private network.", upstream.Host);
    }

    private static string? TryWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
