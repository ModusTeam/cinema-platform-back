
using Cinema.Application.Seats.Commands.CreateSeatType;
using Cinema.Application.Seats.Queries.GetAllSeatTypesQuery;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class SeatTypesController : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        return HandleResult(await Mediator.Send(new GetAllSeatTypesQuery()));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSeatTypeCommand command)
    {
        var result = await Mediator.Send(command);
    
        if (result.IsFailure) return HandleResult(result);
        return StatusCode(StatusCodes.Status201Created, result.Value);
    }
}