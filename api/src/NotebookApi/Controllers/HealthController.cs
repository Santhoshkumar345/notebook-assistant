using Microsoft.AspNetCore.Mvc;
using NotebookApi.Clients;
using NotebookApi.Contracts;

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
    [ProducesResponseType<HealthOut>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<HealthOut>(StatusCodes.Status503ServiceUnavailable, "application/json")]
    public async Task<ActionResult<HealthOut>> Get(CancellationToken ct)
    {
        try
        {
            return new HealthOut("ok", await ai.GetHealthAsync(ct));
        }
        catch (AiServiceException failure)
        {
            logger.LogWarning("Health check: the AI service is not healthy ({Failure})", failure.Failure);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new HealthOut("degraded", null));
        }
    }
}
