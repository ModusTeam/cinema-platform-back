using Cinema.Application.Technologies.Commands.CreateTechnology;
using Cinema.Application.Technologies.Queries.GetAllTechnologies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class TechnologiesController : ApiController
{
    // GET: api/Technologies
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var result = await Mediator.Send(new GetAllTechnologiesQuery());
        return HandleResult(result);
    }

    // POST: api/Technologies
    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTechnologyCommand command)
    {
        var result = await Mediator.Send(command);

        if (result.IsFailure) 
            return HandleResult(result);
        
        return CreatedAtAction(nameof(GetAll), new { id = result.Value }, result.Value);
    }
}