using Cinema.Application.Movies.Commands.DeleteMovie;
using Cinema.Application.Movies.Commands.ImportMovie;
using Cinema.Application.Movies.Commands.UpdateMovie;
using Cinema.Application.Movies.Dtos;
using Cinema.Application.Movies.Queries.GetMovieById;
using Cinema.Application.Movies.Queries.GetMoviesWithPagination;
using Cinema.Application.Movies.Queries.SearchTmdb;
using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class MoviesController : ApiController
{
    [Authorize(Roles = "Admin")]
    [HttpGet("tmdb-search")]
    public async Task<IActionResult> SearchTmdb([FromQuery] string query)
    {
        return Ok(await Mediator.Send(new SearchTmdbQuery(query)));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportMovieCommand commandHandler)
    {
        var result = await Mediator.Send(commandHandler);
        if (result.IsFailure) return HandleResult(result);
        
        return Ok(new { MovieId = result.Value });
    }
    
    [HttpGet]
    public async Task<ActionResult<PaginatedList<MovieDto>>> GetAll([FromQuery] GetMoviesWithPaginationQuery query)
    {
        return HandleResult(await Mediator.Send(query));
    }
    
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        return HandleResult(await Mediator.Send(new GetMovieByIdQuery(id)));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateMovieCommand command)
    {
        if (id != command.Id) return BadRequest("ID mismatch");
        return HandleResult(await Mediator.Send(command));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        return HandleResult(await Mediator.Send(new DeleteMovieCommand(id)));
    }
}