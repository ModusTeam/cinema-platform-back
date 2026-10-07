using Cinema.Application.Common.Interfaces;
using Cinema.Application.Movies.Constants;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Queries.GetMoviesWithPagination;

public record GetMoviesWithPaginationQuery(
    int PageNumber = 1,
    int PageSize = MovieConstants.DefaultPageSize,
    string? SearchTerm = null,
    Guid? GenreId = null 
) : IRequest<Result<PaginatedList<Movie>>>;

public class GetMoviesWithPaginationQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetMoviesWithPaginationQuery, Result<PaginatedList<Movie>>>
{
    public async Task<Result<PaginatedList<Movie>>> Handle(GetMoviesWithPaginationQuery request, CancellationToken ct)
    {
        var query = context.Movies
            .AsNoTracking()
            .Where(m => !m.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = $"%{request.SearchTerm.Trim()}%";
            query = query.Where(m => EF.Functions.ILike(m.Title, term));
        }

        if (request.GenreId.HasValue)
        {
            var genreId = new EntityId<Genre>(request.GenreId.Value);
            query = query.Where(m => m.MovieGenres.Any(mg => mg.GenreId == genreId));
        }
        
        var orderedQuery = query.OrderByDescending(m => m.ReleaseYear).ThenBy(m => m.Title);

        var queryWithGenres = orderedQuery
            .Include(m => m.MovieGenres)
                .ThenInclude(mg => mg.Genre);

        var pagedList = await PaginatedList<Movie>.CreateAsync(
            queryWithGenres.AsSplitQuery(), 
            request.PageNumber, 
            request.PageSize);

        return Result.Success(pagedList);
    }
}