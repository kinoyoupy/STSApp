using Microsoft.AspNetCore.Mvc;
using STSApp.Backend.Health;

namespace STSApp.Backend.Controllers;

/// <summary>
/// 開発中にBackendからMySQLへ接続できるか確認するためのAPIです。
/// </summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly DatabaseHealthCheck _databaseHealthCheck;

    public HealthController(DatabaseHealthCheck databaseHealthCheck)
    {
        _databaseHealthCheck = databaseHealthCheck;
    }

    [HttpGet("database")]
    public async Task<IActionResult> Database(CancellationToken cancellationToken)
    {
        var canConnect = await _databaseHealthCheck.CanConnectAsync(cancellationToken);
        if (canConnect)
        {
            return Ok(new { status = "ok" });
        }

        return Problem(
            title: "Database unavailable",
            detail: "The backend could not connect to MySQL. Check ConnectionStrings:MySql and Docker status.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
