using System.Security.Claims;

namespace NotebookApi.Infrastructure;

/// <summary>Who is making this request. Null when nobody is signed in.</summary>
public interface ICurrentUser
{
    Guid? Id { get; }
}

public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id => AmbientUser.Current ?? FromPrincipal(accessor.HttpContext?.User);

    /// <summary>The signed-in user's id from a principal, or null for an anonymous one. Also used by the handlers that cannot take scoped services.</summary>
    public static Guid? FromPrincipal(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
