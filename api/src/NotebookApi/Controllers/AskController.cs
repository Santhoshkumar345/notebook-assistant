using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NotebookApi.Clients;
using NotebookApi.Contracts;
using NotebookApi.Infrastructure;
using NotebookApi.Services;

namespace NotebookApi.Controllers;

[ApiController]
[Route("api/ask")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContent.Type)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemContent.Type)]
[EnableRateLimiting("ask")]
public sealed class AskController(IAiServiceClient ai, ConversationService conversations) : ControllerBase
{
    /// <summary>
    /// Ask a question about the stored documents and save the exchange. Without a conversationId a new conversation is started.
    /// mode "agent" may answer with a clarifying question (status "clarify"); the person's answer is sent with answersClarification
    /// true, so the AI service gets the original question together with it. A failed ask is not saved.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<AskResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemContent.Type)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout, ProblemContent.Type)]
    public async Task<ActionResult<AskResponse>> Ask(AskIn request, CancellationToken ct)
    {
        // Everything that can be refused is refused before the model call, so a bad request does not cost one.
        var forAiService = request;
        if (request.AnswersClarification)
        {
            if (request.ConversationId is not { } conversationId)
                return Invalid(nameof(request.ConversationId), "A conversationId is required when answersClarification is true.");

            var composed = await conversations.ComposeClarifiedQuestionAsync(conversationId, request.Question, ct);
            switch (composed.Outcome)
            {
                case ClarificationOutcome.NoSuchConversation:
                    return NoSuchConversation();
                case ClarificationOutcome.NotAwaitingAnswer:
                    return Problem(statusCode: StatusCodes.Status409Conflict, title: "The conversation is not waiting for an answer to a clarifying question.");
                case ClarificationOutcome.TooLong:
                    return Invalid(nameof(request.Question), $"Together with the earlier question, the answer is longer than {Limits.MaxQuestionChars} characters.");
                default:
                    forAiService = request.WithQuestion(composed.Question!);
                    break;
            }
        }
        else if (request.ConversationId is { } id && !await conversations.ExistsAsync(id, ct))
        {
            return NoSuchConversation();
        }

        if (await conversations.QuotaProblemAsync(request.ConversationId, ct) is { } quotaProblem)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: quotaProblem);

        // A follow-up is rewritten upstream into a question that stands on its own, from this person's own saved turns. Answering a clarifying question already
        // carries its context (the combined question above), and a first question has none.
        var history = request.AnswersClarification || request.ConversationId is not { } conversation ? null : await conversations.RecentTurnsAsync(conversation, ct);

        var answer = await ai.AskAsync(forAiService, history, ct);

        // What is saved is what the person typed, not the combined text the AI service was given.
        var requestId = HttpContext.Items[CorrelationIdMiddleware.Header] as string;
        var saved = await conversations.SaveExchangeAsync(request.ConversationId, request, answer, requestId, ct);
        return saved is null ? NoSuchConversation() : saved;
    }

    private ObjectResult NoSuchConversation() => Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation.");

    private ActionResult Invalid(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }
}
