using Microsoft.AspNetCore.Identity;

namespace NotebookApi.Data;

/// <summary>A person with an account. The email is also the user name. The id doubles as the tenant id in the AI service.</summary>
public sealed class AppUser : IdentityUser<Guid>;

/// <summary>Something that belongs to exactly one user. Every such type has a query filter (a test checks that none is missed).</summary>
public interface IOwned
{
    Guid UserId { get; set; }
}
