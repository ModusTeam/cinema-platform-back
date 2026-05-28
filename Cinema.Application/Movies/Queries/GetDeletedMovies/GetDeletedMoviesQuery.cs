using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Queries.GetDeletedMovies;

public record GetDeletedMoviesQuery(
    int PageNumber = 1,
    int PageSize = 20
) : IRequest<Result<PaginatedList<Movie>>>;

public class GetDeletedMoviesQueryHandler(IApplicationDbContext context)
    : IRequestHandler<GetDeletedMoviesQuery, Result<PaginatedList<Movie>>>
{
    public async Task<Result<PaginatedList<Movie>>> Handle(GetDeletedMoviesQuery request, CancellationToken ct)
    {
        var query = context.Movies
            .AsNoTracking()
            .Where(m => m.IsDeleted)
            .OrderByDescending(m => m.ReleaseYear)
            .ThenBy(m => m.Title)
            .Include(m => m.MovieGenres)
                .ThenInclude(mg => mg.Genre);

        var pagedList = await PaginatedList<Movie>.CreateAsync(
            query.AsSplitQuery(),
            request.PageNumber,
            request.PageSize);

        return Result.Success(pagedList);
    }
}
