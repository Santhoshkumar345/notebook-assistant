using Microsoft.AspNetCore.Mvc;
using NotebookApi.Clients;
using NotebookApi.Contracts;
using NotebookApi.Infrastructure;
using NotebookApi.Services;

namespace NotebookApi.Controllers;

[ApiController]
[Route("api/ask")]
public sealed class AskController(IAiServiceClient ai, ConversationService conversations) : ControllerBase
{
    /// <summary>
    /// Ask a question about the stored documents and save the exchange. Without a conversationId a new conversation is started.
    /// mode "agent" may answer with a clarifying question (status "clarify"). A failed ask is not saved.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<AskResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout, ProblemContent.Type)]
    public async Task<ActionResult<AskResponse>> Ask(AskIn request, CancellationToken ct)
    {
        // Checked before the model call, so an unknown conversation does not cost one.
        if (request.ConversationId is { } id && !await conversations.ExistsAsync(id, ct))
            return NoSuchConversation();

        var answer = await ai.AskAsync(request, ct);

        var requestId = HttpContext.Items[CorrelationIdMiddleware.Header] as string;
        var saved = await conversations.SaveExchangeAsync(request.ConversationId, request, answer, requestId, ct);
        return saved is null ? NoSuchConversation() : saved;
    }

    private ObjectResult NoSuchConversation() => Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation.");
}
