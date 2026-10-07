using Cinema.Application.Halls.Commands.CreateHall;
using Cinema.Application.Halls.Commands.DeleteHall;
using Cinema.Application.Halls.Commands.UpdateHall;
using Cinema.Application.Halls.Dtos;
using Cinema.Application.Halls.Queries.GetHallById;
using Cinema.Application.Halls.Queries.GetHallLookups;
using Cinema.Application.Halls.Queries.GetHallsWithPagination;
using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class HallsController : ApiController
{
    [HttpGet]
    public async Task<ActionResult<PaginatedList<HallDto>>> GetAll([FromQuery] GetHallsWithPaginationQuery query)
    {
        return HandleResult(await Mediator.Send(query));
    }

    [HttpGet("lookups")]
    public async Task<ActionResult<List<HallLookupDto>>> GetLookups()
    {
        return HandleResult(await Mediator.Send(new GetActiveHallsLookupQuery()));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        return HandleResult(await Mediator.Send(new GetHallByIdQuery(id)));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create(CreateHallCommand command)
    {
        var result = await Mediator.Send(command);

        if (result.IsFailure)
            return HandleResult(result);
        return CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value);
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateHallCommand command)
    {
        if (id != command.HallId) return BadRequest("ID mismatch");
        return HandleResult(await Mediator.Send(command));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}/technologies")]
    public async Task<IActionResult> UpdateTechnologies(Guid id, [FromBody] UpdateHallTechnologiesCommand command)
    {
        if (id != command.HallId) return BadRequest("ID mismatch");
        return HandleResult(await Mediator.Send(command));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        return HandleResult(await Mediator.Send(new DeleteHallCommand(id)));
    }
}