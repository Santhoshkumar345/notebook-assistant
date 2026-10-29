using Microsoft.AspNetCore.Mvc;
using NotebookApi.Contracts;
using NotebookApi.Services;

namespace NotebookApi.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(DocumentService documents) : ControllerBase
{
    /// <summary>The stored documents, with whether each one made it into the search index (status Indexed) or needs attention (Failed).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DocumentInfo>>> List(CancellationToken ct) =>
        Ok(await documents.ListAsync(ct));

    /// <summary>Add a markdown document (201) or replace one with the same name (200). If the AI service cannot index it the document is kept with status Failed and the call fails; POST .../reindex retries.</summary>
    [HttpPost]
    public async Task<ActionResult<DocumentInfo>> Add(DocumentIn document, CancellationToken ct)
    {
        var (stored, created) = await documents.UpsertAsync(document, ct);
        return created
            ? Created($"/api/documents/{Uri.EscapeDataString(stored.Name)}", stored)
            : Ok(stored);
    }

    /// <summary>Re-send a stored document to the AI service, for example after a Failed upload.</summary>
    [HttpPost("{name}/reindex")]
    public async Task<ActionResult<DocumentInfo>> Reindex(string name, CancellationToken ct)
    {
        if (InvalidName(name) is { } invalid) return invalid;
        return await documents.ReindexAsync(name, ct) is { } stored
            ? Ok(stored)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "No such document.");
    }

    /// <summary>Make the AI service's index match what is stored here: remove documents it holds that are not stored, and re-send every stored one. Safe to repeat.</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<SyncResult>> Sync(CancellationToken ct) =>
        Ok(await documents.SyncAsync(ct));

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (InvalidName(name) is { } invalid) return invalid;
        return await documents.DeleteAsync(name, ct)
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "No such document.");
    }

    // Route values are not covered by body validation, so the rule is applied here.
    private ActionResult? InvalidName(string name)
    {
        if (Limits.DocumentName().IsMatch(name)) return null;
        ModelState.AddModelError(nameof(name), "The name must be a markdown file name such as notes.md.");
        return ValidationProblem(ModelState);
    }
}
