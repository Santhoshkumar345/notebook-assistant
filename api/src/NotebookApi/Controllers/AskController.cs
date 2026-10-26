using Microsoft.AspNetCore.Mvc;
using NotebookApi.Clients;
using NotebookApi.Contracts;

namespace NotebookApi.Controllers;

[ApiController]
[Route("api/ask")]
public sealed class AskController(IAiServiceClient ai) : ControllerBase
{
    /// <summary>Ask a question about the uploaded documents. mode "agent" may answer with a clarifying question (status "clarify").</summary>
    [HttpPost]
    public async Task<ActionResult<AskOut>> Ask(AskIn request, CancellationToken ct) =>
        await ai.AskAsync(request, ct);
}
