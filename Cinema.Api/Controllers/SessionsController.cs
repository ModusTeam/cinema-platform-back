using Cinema.Application.Sessions.Commands.CancelSession;
using Cinema.Application.Sessions.Commands.CreateSession;
using Cinema.Application.Sessions.Commands.RescheduleSession;
using Cinema.Application.Sessions.Dtos;
using Cinema.Application.Sessions.Queries.GetSessionById;
using Cinema.Application.Sessions.Queries.GetSessionsByDateQuery;
using Cinema.Application.Sessions.Queries.GetSessionSeats;
using Cinema.Application.Sessions.Queries.GetSessionsWithPaginationQuery;
using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class SessionsController : ApiController
{
    [HttpGet]
    public async Task<ActionResult<PaginatedList<SessionDto>>> GetAll([FromQuery] GetSessionsWithPaginationQuery query)
    {
        return HandleResult(await Mediator.Send(query));
    }
    
    [HttpGet("by-date")]
    public async Task<ActionResult<List<SessionDto>>> GetByDate([FromQuery] DateTime? date)
    {
        return HandleResult(await Mediator.Send(new GetSessionsByDateQuery(date)));
    }
    
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SessionDto>> GetById(Guid id)
    {
        return HandleResult(await Mediator.Send(new GetSessionByIdQuery(id)));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create(CreateSessionCommand command)
    {
        var result = await Mediator.Send(command);

        if (result.IsFailure) return HandleResult(result);

        return CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value);
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}/reschedule")]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleSessionCommand command)
    {
        if (id != command.SessionId) return BadRequest("Session ID mismatch");
        return HandleResult(await Mediator.Send(command));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        return HandleResult(await Mediator.Send(new CancelSessionCommand(id)));
    }
    
    [HttpGet("{id}/seats")]
    [ProducesResponseType(typeof(SessionSeatsVm), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSeats(Guid id)
    {
        var result = await Mediator.Send(new GetSessionSeatsQuery(id));
        return HandleResult(result);
    }
}