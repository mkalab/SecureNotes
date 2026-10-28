using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Data;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly ILogger<HealthController> _logger;

    public HealthController(IUserRepository users, ILogger<HealthController> logger)
    {
        _users = users;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get()
    {
        return Ok(new HealthResponse("ok"));
    }

    [HttpGet("ready")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HealthResponse>> GetReadiness(CancellationToken cancellationToken)
    {
        try
        {
            await _users.GetByEmailAsync("p1-readiness-check@invalid.local", cancellationToken);
            return Ok(new HealthResponse("ready"));
        }
        catch (SqlException exception)
        {
            _logger.LogError(exception, "Database readiness check failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new HealthResponse("unavailable"));
        }
    }

    public sealed record HealthResponse(string Status);
}
