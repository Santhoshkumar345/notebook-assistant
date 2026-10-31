using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NotebookApi.Contracts;
using NotebookApi.Data;
using NotebookApi.Infrastructure;

namespace NotebookApi.Controllers;

/// <summary>
/// Local accounts: an email and a password, a session in an HttpOnly, SameSite=Strict cookie. There is no email verification and no password
/// reset (nothing here can send email), and registering an email that is taken says so, which reveals that the address has an account;
/// both are recorded in docs/architecture.md. Sign-in and registration are rate limited per address, and a password is checked even
/// for an unknown email so the response time does not reveal whether the account exists.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    IPasswordHasher<AppUser> hasher,
    IOptions<AuthOptions> options) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<UserInfo>(StatusCodes.Status201Created, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemContent.Type)]
    public async Task<ActionResult<UserInfo>> Register(RegisterIn request)
    {
        if (options.Value.InviteCode is { Length: > 0 } expected && !InviteMatches(request.InviteCode, expected))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "A valid invite code is required.");

        var email = request.Email.Trim();
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
            return Rejected(created);

        await signIn.SignInAsync(user, isPersistent: false);
        Csrf.Issue(HttpContext); // a token for the person who is now signed in
        return Created("/api/auth/me", new UserInfo(user.Id, email));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<UserInfo>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemContent.Type)]
    public async Task<ActionResult<UserInfo>> Login(LoginIn request)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            hasher.HashPassword(new AppUser(), request.Password); // the same work as a real check, so an unknown email is not faster to refuse
            return BadCredentials();
        }

        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            var until = await users.GetLockoutEndDateAsync(user);
            var wait = until is { } end ? Math.Max(1, (int)Math.Ceiling((end - DateTimeOffset.UtcNow).TotalSeconds)) : 60;
            Response.Headers.RetryAfter = wait.ToString();
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed attempts. Try again later.");
        }

        if (!result.Succeeded)
            return BadCredentials();

        await signIn.SignInAsync(user, isPersistent: false);
        Csrf.Issue(HttpContext);
        return new UserInfo(user.Id, user.Email!);
    }

    /// <summary>Ends the session. Safe to call when nobody is signed in.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        // SignOutAsync leaves HttpContext.User as the person who just signed out for the rest of this request, and a token is bound to HttpContext.User,
        // so without this line the "signed-out page" token would still name them and the next sign-in would be refused.
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        Csrf.Issue(HttpContext); // a token for the signed-out page, so signing in again works without a reload
        return NoContent();
    }

    /// <summary>Who is signed in. The page calls this on load and after signing in; it also issues the CSRF token for the current identity.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserInfo>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContent.Type)]
    public async Task<ActionResult<UserInfo>> Me()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) // the account was deleted while the cookie was still valid
        {
            await signIn.SignOutAsync();
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in required.");
        }

        return new UserInfo(user.Id, user.Email!);
    }

    private ActionResult BadCredentials() =>
        Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Email or password is incorrect."); // the same words whether the email exists or not

    private ActionResult Rejected(IdentityResult result)
    {
        var codes = result.Errors.Select(e => e.Code).ToList();
        if (codes.Any(c => c is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "That email address is already registered.");

        foreach (var error in result.Errors)
        {
            var field = error.Code.StartsWith("Password", StringComparison.Ordinal) ? nameof(RegisterIn.Password) : nameof(RegisterIn.Email);
            ModelState.AddModelError(field, error.Description);
        }

        return ValidationProblem(ModelState);
    }

    // Compared in constant time, so the code cannot be worked out one character at a time from how long a wrong guess takes.
    private static bool InviteMatches(string? supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied ?? ""), Encoding.UTF8.GetBytes(expected));
}
