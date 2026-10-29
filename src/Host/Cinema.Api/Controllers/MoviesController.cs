using Cinema.Api.DTOs.Movies;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Services;
using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Movies.Commands.CreateMovie;
using Cinema.Catalog.Application.Movies.Commands.DeleteMovie;
using Cinema.Catalog.Application.Movies.Commands.ImportMovie;
using Cinema.Catalog.Application.Movies.Commands.RefreshMovieFromTmdb;
using Cinema.Catalog.Application.Movies.Commands.RestoreMovie;
using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Catalog.Application.Movies.Queries.GetDeletedMovies;
using Cinema.Catalog.Application.Movies.Queries.GetMovieById;
using Cinema.Catalog.Application.Movies.Queries.GetMoviesWithPagination;
using Cinema.Application.Movies.Queries.GetRecommendations;
using Cinema.Catalog.Application.Movies.Queries.SearchTmdb;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Hangfire;
using Mapster;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace Cinema.Api.Controllers;

public class MoviesController : ApiController
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    
    public MoviesController(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient;
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("images/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MovieImageUploadService.MaxFileBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MovieImageUploadService.MaxFileBytes + 64 * 1024,
        ValueCountLimit = 2, ValueLengthLimit = 64, MultipartHeadersLengthLimit = 4096)]
    [ProducesResponseType(typeof(MovieImageUploadResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> UploadImage([FromForm] UploadMovieImageRequest request,
        [FromServices] MovieImageUploadService uploader, CancellationToken ct)
    {
        await using Stream? content = request.File?.OpenReadStream();
        return HandleResult(await uploader.UploadAsync(content, request.Kind, ct));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("tmdb-search")]
    public async Task<IActionResult> SearchTmdb([FromQuery] string query)
    {
        var result = await Mediator.Send(new SearchTmdbQuery(query));
        return Ok(result.Adapt<List<TmdbSearchResultDto>>());
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMovieCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPost("import")]
    public IActionResult Import([FromBody] ImportMovieCommand command)
    {
        var jobId = $"import-movie-{command.TmdbId}";
        
        _backgroundJobClient.Enqueue<ISender>(mediator => 
            mediator.Send(command, CancellationToken.None));
    
        return Accepted(new { Message = "Import started", JobId = jobId });
    }
    
    [HttpGet]
    [OutputCache(Duration = 60, VaryByQueryKeys = new[] { "pageNumber", "pageSize", "searchTerm", "genreId" })]
    public async Task<ActionResult<PaginatedList<MovieListDto>>> GetMovies([FromQuery] GetMoviesWithPaginationQuery query)
    {
        var result = await Mediator.Send(query);
        if (result.IsFailure)
        {
            return HandleResult(Result.Failure<PaginatedList<MovieListDto>>(result.Error));
        }

        var movieDtos = result.Value.Items.Adapt<List<MovieListDto>>();
        var paginatedDtos = new PaginatedList<MovieListDto>(
            movieDtos,
            result.Value.TotalCount,
            result.Value.PageNumber,
            query.PageSize);

        return Ok(paginatedDtos);
    }
    
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await Mediator.Send(new GetMovieByIdQuery(id));
        var mappedResult = result.IsSuccess 
            ? Result.Success(result.Value.Adapt<MovieDto>()) 
            : Result.Failure<MovieDto>(result.Error);
        return HandleResult(mappedResult);
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPatch("{id:guid}/title")]
    public async Task<IActionResult> RenameMovie(Guid id, [FromBody] RenameMovieDto dto)
    {
        return HandleResult(await Mediator.Send(new RenameMovieCommand(id, dto.Title)));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPatch("{id:guid}/images")]
    public async Task<IActionResult> UpdateImages(Guid id, [FromBody] UpdateMovieImagesDto dto)
    {
        return HandleResult(await Mediator.Send(
            new UpdateMovieImagesCommand(id, dto.PosterUrl, dto.BackdropUrl, dto.TrailerUrl)));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPatch("{id:guid}/details")]
    public async Task<IActionResult> UpdateDetails(Guid id, [FromBody] UpdateMovieDetailsDto dto)
    {
        return HandleResult(await Mediator.Send(
            new UpdateMovieDetailsCommand(id, dto.Description, dto.DurationMinutes, dto.Rating, dto.ReleaseYear)));
    }
    
    [Authorize(Roles = "Admin")]
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] MovieStatus status)
    {
        return HandleResult(await Mediator.Send(new UpdateMovieStatusCommand(id, status)));
    }
    

    [Authorize(Roles = "Admin")]
    [HttpPost("{id:guid}/refresh-tmdb")]
    public async Task<IActionResult> RefreshFromTmdb(Guid id)
    {
        return HandleResult(await Mediator.Send(new RefreshMovieFromTmdbCommand(id)));
    }
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        return HandleResult(await Mediator.Send(new DeleteMovieCommand(id)));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("deleted")]
    public async Task<ActionResult<PaginatedList<MovieListDto>>> GetDeleted([FromQuery] GetDeletedMoviesQuery query)
    {
        var result = await Mediator.Send(query);
        if (result.IsFailure)
            return HandleResult(Result.Failure<PaginatedList<MovieListDto>>(result.Error));

        var movieDtos = result.Value.Items.Adapt<List<MovieListDto>>();
        var paginatedDtos = new PaginatedList<MovieListDto>(
            movieDtos,
            result.Value.TotalCount,
            result.Value.PageNumber,
            query.PageSize);

        return Ok(paginatedDtos);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id)
    {
        return HandleResult(await Mediator.Send(new RestoreMovieCommand(id)));
    }
    
    [HttpGet("recommendations")]
    [Authorize]
    public async Task<IActionResult> GetRecommendations([FromQuery] int count = 5)
    {
        var query = new GetPersonalizedRecommendationsQuery(count);
        var result = await Mediator.Send(query);

        if (result.IsFailure)
        {
            return HandleResult(Result.Failure<List<MovieRecommendationDto>>(result.Error));
        }

        return Ok(result.Value.Adapt<List<MovieRecommendationDto>>());
    }
}

