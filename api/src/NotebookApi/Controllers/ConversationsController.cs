using Microsoft.AspNetCore.Mvc;
using NotebookApi.Contracts;
using NotebookApi.Services;

namespace NotebookApi.Controllers;

[ApiController]
[Route("api/conversations")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContent.Type)]
public sealed class ConversationsController(ConversationService conversations) : ControllerBase
{
    /// <summary>Most recently active first. limit is 1 to 100 (default 50); values outside that range are clamped.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ConversationSummary>>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<IReadOnlyList<ConversationSummary>>> List([FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await conversations.ListAsync(limit, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ConversationOut>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContent.Type)]
    public async Task<ActionResult<ConversationOut>> Get(Guid id, CancellationToken ct) =>
        await conversations.GetAsync(id, ct) is { } found
            ? Ok(found)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContent.Type)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await conversations.DeleteAsync(id, ct)
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation.");
}
