using Cinema.Application.Stats.Queries.GetDashboardStats;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[Authorize(Roles = "Admin")]
public class StatsController : ApiController
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboardStats([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var query = new GetDashboardStatsQuery(from, to);
        return HandleResult(await Mediator.Send(query));
    }
}