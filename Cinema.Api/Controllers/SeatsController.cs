using Cinema.Application.Seats.Commands.BatchChangeSeatType;
using Cinema.Application.Seats.Commands.LockSeat;
using Cinema.Application.Seats.Commands.UpdateSeat;
using Cinema.Application.Seats.Commands.UpdateSeatStatus;
using Cinema.Application.Seats.Dtos;
using Cinema.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class SeatsController : ApiController
{
    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}/type")]
    public async Task<IActionResult> ChangeType(Guid id, [FromBody] ChangeSeatTypeCommand command)
    {
        if (id != command.SeatId) return BadRequest("ID mismatch");
        return HandleResult(await Mediator.Send(command));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPut("batch-change-type")]
    public async Task<IActionResult> BatchChangeType([FromBody] BatchChangeSeatTypeCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }
    
    [HttpPost("lock")]
    [Authorize] 
    public async Task<IActionResult> LockSeat([FromBody] LockSeatRequest request)
    {
        var command = new LockSeatCommand(request.SessionId, request.SeatId, UserId);
        return HandleResult(await Mediator.Send(command));
    }

    [Authorize(Roles = "Admin")]
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateSeatStatusRequest request)
    {
        var command = new UpdateSeatStatusCommand(id, request.Status);
        var result = await Mediator.Send(command);
        return HandleResult(result);
    }
}

public record UpdateSeatStatusRequest(SeatStatus Status);
