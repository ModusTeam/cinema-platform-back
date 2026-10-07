using Cinema.Application.Achievements.Commands;
using Cinema.Application.Achievements.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Cinema.API.Controllers;

[ApiController]
[Route("api/achievements")]
public class AchievementsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool includeInactive = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(
            new GetAdminAchievementsQuery(includeInactive, limit, offset), ct);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(
        [FromBody] CreateAchievementCommand command,
        CancellationToken ct = default)
    {
        var achievement = await mediator.Send(command, ct);
        return CreatedAtAction(nameof(GetAll), new { }, achievement);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdateAchievementCommand command,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(command with { Id = id }, ct);
        return Ok(result);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(
        string id, CancellationToken ct = default)
    {
        var result = await mediator.Send(new DeleteAchievementCommand(id), ct);
        return Ok(result);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetMyAchievements(
        [FromQuery] bool includeLocked = true,
        CancellationToken ct = default)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await mediator.Send(
            new GetUserAchievementsQuery(userId, includeLocked), ct);
        return Ok(result);
    }
}