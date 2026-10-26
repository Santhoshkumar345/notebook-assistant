using Microsoft.AspNetCore.Mvc;
using NotebookApi.Clients;
using NotebookApi.Contracts;

namespace NotebookApi.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(IAiServiceClient ai) : ControllerBase
{
    /// <summary>Document names with their chunk counts.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyDictionary<string, int>>> List(CancellationToken ct) =>
        Ok(await ai.GetDocumentsAsync(ct));

    /// <summary>Add a markdown document, or replace one with the same name.</summary>
    [HttpPost]
    public async Task<ActionResult<DocumentOut>> Add(DocumentIn document, CancellationToken ct)
    {
        var created = await ai.AddDocumentAsync(document, ct);
        return Created($"/api/documents/{Uri.EscapeDataString(created.Name)}", created);
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        // Route values are not covered by body validation, so the rule is applied here.
        if (!Limits.DocumentName().IsMatch(name))
        {
            ModelState.AddModelError(nameof(name), "The name must be a markdown file name such as notes.md.");
            return ValidationProblem(ModelState);
        }

        return await ai.DeleteDocumentAsync(name, ct)
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "No such document.");
    }
}
