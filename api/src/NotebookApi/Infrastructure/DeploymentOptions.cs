using System.ComponentModel.DataAnnotations;
using System.Net;

namespace NotebookApi.Infrastructure;

/// <summary>Which proxies' X-Forwarded-For / X-Forwarded-Proto headers to believe.</summary>
public enum ForwardedMode
{
    /// <summary>Believe none: the app is reached directly. Anyone could otherwise claim any address or scheme.</summary>
    None,

    /// <summary>Believe only the proxies in KnownProxies and KnownNetworks.</summary>
    TrustedProxies,

    /// <summary>
    /// Believe whoever connects. Only safe when the app can be reached from nowhere but the platform's own proxy (a container behind a managed
    /// load balancer, on a private port). If anything else can connect, it can forge its address, and so dodge the per-address rate limit.
    /// </summary>
    TrustAll,
}

/// <summary>
/// Behind a proxy that ends TLS, the API sees plain http from the proxy's address. Without these headers it would think the connection is insecure
/// (cookies marked Secure would be refused, and the antiforgery system fails outright) and every visitor would share the proxy's address for rate limiting.
/// </summary>
public sealed class ProxyOptions : IValidatableObject
{
    public const string Section = "ForwardedHeaders";

    public ForwardedMode Mode { get; set; } = ForwardedMode.None;

    /// <summary>Single addresses, for Mode TrustedProxies.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Address ranges in CIDR form such as 10.0.0.0/8, for Mode TrustedProxies.</summary>
    public string[] KnownNetworks { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Mode == ForwardedMode.TrustedProxies && KnownProxies.Length + KnownNetworks.Length == 0)
            yield return new ValidationResult("Mode TrustedProxies needs at least one entry in KnownProxies or KnownNetworks.", [nameof(Mode)]);

        foreach (var proxy in KnownProxies.Where(p => !IPAddress.TryParse(p, out _)))
            yield return new ValidationResult($"'{proxy}' is not an IP address.", [nameof(KnownProxies)]);

        foreach (var network in KnownNetworks.Where(n => !System.Net.IPNetwork.TryParse(n, out _)))
            yield return new ValidationResult($"'{network}' is not a network in CIDR form (for example 10.0.0.0/8).", [nameof(KnownNetworks)]);
    }
}

/// <summary>What one person may store. Without limits a single account could fill the disk or the index.</summary>
public sealed class QuotaOptions
{
    public const string Section = "Quotas";

    [Range(1, 100_000)]
    public int MaxDocuments { get; set; } = 50;

    /// <summary>Characters across all of one person's documents. The text is also embedded, so this bounds the index as well as the database.</summary>
    [Range(1, 1_000_000_000)]
    public long MaxTotalDocumentChars { get; set; } = 2_000_000;

    [Range(1, 1_000_000)]
    public int MaxConversations { get; set; } = 200;

    /// <summary>Turns, so a question and its answer count as two.</summary>
    [Range(2, 1_000_000)]
    public int MaxMessagesPerConversation { get; set; } = 400;
}

/// <summary>
/// The AI service's index is derived from the database, but a host with a disk that does not last (most free ones) loses it on every restart,
/// while the database still says "Indexed". The reconciler notices and re-sends what is missing.
/// </summary>
public sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    public bool Enabled { get; set; } = true;

    [Range(1, 1_440)]
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Wait before the first pass, so the AI service has a chance to be up when both start together.</summary>
    [Range(0, 3_600)]
    public int StartupDelaySeconds { get; set; } = 15;
}

public sealed class KeyStorageOptions
{
    public const string Section = "DataProtection";

    /// <summary>
    /// Where the keys that encrypt the sign-in cookie and the CSRF tokens are kept. Default: a "keys" folder next to the database, so one persistent
    /// volume holds both. If these keys are lost, everyone is signed out. They are not encrypted at rest here, so keep the folder private.
    /// </summary>
    public string? KeysPath { get; set; }
}

public sealed class HttpLimitOptions
{
    public const string Section = "Limits";

    /// <summary>
    /// A document may hold 200,000 characters, up to about 1.2 MB once JSON-escaped in the worst case. Anything larger is refused before it is read,
    /// where the host default is 30 MB.
    /// </summary>
    [Range(1_024, 100_000_000)]
    public long MaxRequestBodyBytes { get; set; } = 2 * 1024 * 1024;
}
