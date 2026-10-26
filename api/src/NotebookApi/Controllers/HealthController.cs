using Microsoft.AspNetCore.Mvc;
using NotebookApi.Clients;

namespace NotebookApi.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController(IAiServiceClient ai, ILogger<HealthController> logger) : ControllerBase
{
    /// <summary>
    /// 200 when this API and the AI service are both up; 503 with status "degraded" when the AI service cannot be reached.
    /// A health check reports a problem instead of failing with an error, so a monitor can read the body.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        try
        {
            return Ok(new { status = "ok", ai = await ai.GetHealthAsync(ct) });
        }
        catch (AiServiceException failure)
        {
            logger.LogWarning("Health check: the AI service is not healthy ({Failure})", failure.Failure);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "degraded", ai = (object?)null });
        }
    }
}
