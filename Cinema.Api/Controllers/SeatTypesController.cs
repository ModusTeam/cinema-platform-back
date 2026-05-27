
using Cinema.Application.Seats.Commands.CreateSeatType;
using Cinema.Application.Seats.Commands.UpdateSeatType;
using Cinema.Application.Seats.Queries.GetAllSeatTypesQuery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class SeatTypesController : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        return HandleResult(await Mediator.Send(new GetAllSeatTypesQuery()));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSeatTypeCommand command)
    {
        var result = await Mediator.Send(command);
    
        if (result.IsFailure) return HandleResult(result);
        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSeatTypeCommand command)
    {
        if (id != command.Id) return BadRequest("ID mismatch.");
        return HandleResult(await Mediator.Send(command));
    }
}