using Cinema.Application.Genres.Commands.CreateGenre;
using Cinema.Application.Genres.Commands.DeleteGenre;
using Cinema.Application.Genres.Commands.UpdateGenre;
using Cinema.Application.Genres.Dtos;
using Cinema.Application.Genres.Queries.GetGenres;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

public class GenresController : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        return Ok(await Mediator.Send(new GetGenresQuery()));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGenreCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateGenreDto dto)
    {
        return HandleResult(await Mediator.Send(new UpdateGenreCommand(id, dto.Name)));
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) 
    {
        return HandleResult(await Mediator.Send(new DeleteGenreCommand(id)));
    }
}