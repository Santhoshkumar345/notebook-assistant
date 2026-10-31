using Microsoft.Extensions.Options;

namespace NotebookApi.Infrastructure;

/// <summary>
/// Proves to the AI service that the caller is this API (the service key) and says whose data the call is about (the tenant: the signed-in
/// user's id, 32 hex characters). The AI service trusts the tenant header because only the key holder can send it, which is why the key
/// is required at startup and the tenant comes from the signed-in user, never from anything the browser sent.
/// A handler cannot take the request's scoped services, so it reads the user from the HTTP context accessor.
/// </summary>
public sealed class AiServiceCallerHandler(IHttpContextAccessor accessor, IOptions<AiServiceOptions> options) : DelegatingHandler
{
    public const string KeyHeader = "X-Service-Key";
    public const string TenantHeader = "X-Tenant-ID";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(KeyHeader);
        request.Headers.Remove(TenantHeader);
        request.Headers.TryAddWithoutValidation(KeyHeader, options.Value.ApiKey);

        // No tenant for a call made with nobody signed in (the anonymous health check): the AI service needs none for /health, and
        // refuses a document or question call that arrives without one.
        if (HttpContextCurrentUser.FromPrincipal(accessor.HttpContext?.User) is { } user)
            request.Headers.TryAddWithoutValidation(TenantHeader, user.ToString("N"));

        return base.SendAsync(request, cancellationToken);
    }
}
