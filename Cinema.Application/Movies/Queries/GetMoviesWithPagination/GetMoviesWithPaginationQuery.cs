using Cinema.Application.Common.Interfaces;
using Cinema.Application.Movies.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Queries.GetMoviesWithPagination;

public record GetMoviesWithPaginationQuery(
    int PageNumber = 1,
    int PageSize = 10,
    string? SearchTerm = null
) : IRequest<Result<PaginatedList<MovieDto>>>;

public class GetMoviesWithPaginationQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetMoviesWithPaginationQuery, Result<PaginatedList<MovieDto>>>
{
    public async Task<Result<PaginatedList<MovieDto>>> Handle(GetMoviesWithPaginationQuery request, CancellationToken ct)
    {
        var query = context.Movies
            .AsNoTracking()
            .Include(m => m.MovieGenres)
            .ThenInclude(mg => mg.Genre)
            .AsQueryable();
        
        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = request.SearchTerm.Trim().ToLower();
            query = query.Where(m => m.Title.ToLower().Contains(term));
        }
        
        query = query.OrderByDescending(m => m.ReleaseYear).ThenBy(m => m.Title);
        
        var dtoQuery = query.Select(m => MovieDto.FromDomain(m));
        
        var pagedList = await PaginatedList<MovieDto>.CreateAsync(
            dtoQuery, 
            request.PageNumber, 
            request.PageSize);

        return Result.Success(pagedList);
    }
}