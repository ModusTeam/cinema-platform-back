using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Errors;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Queries.GetMovieById;

public record GetMovieByIdQuery(Guid Id) : IRequest<Result<Movie>>;

public class GetMovieByIdQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetMovieByIdQuery, Result<Movie>>
{
    public async Task<Result<Movie>> Handle(GetMovieByIdQuery request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);

        var movie = await context.Movies
            .AsNoTracking()
            .Include(m => m.MovieGenres)
                .ThenInclude(mg => mg.Genre)
            .FirstOrDefaultAsync(m => m.Id == movieId, ct);
        if (movie == null)
            return Result.Failure<Movie>(MovieErrors.NotFound);

        return Result.Success(movie);
    }
}