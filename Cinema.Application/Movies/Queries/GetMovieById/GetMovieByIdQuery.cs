using Cinema.Application.Common.Interfaces;
using Cinema.Application.Movies.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Queries.GetMovieById;

public record GetMovieByIdQuery(Guid Id) : IRequest<Result<MovieDto>>;

public class GetMovieByIdQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetMovieByIdQuery, Result<MovieDto>>
{
    public async Task<Result<MovieDto>> Handle(GetMovieByIdQuery request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);

        var movie = await context.Movies
            .AsNoTracking()
            .Include(m => m.MovieGenres)
            .ThenInclude(mg => mg.Genre)
            .FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie == null)
            return Result.Failure<MovieDto>(new Error("Movie.NotFound", "Movie not found."));

        return Result.Success(movie.Adapt<MovieDto>());
    }
}