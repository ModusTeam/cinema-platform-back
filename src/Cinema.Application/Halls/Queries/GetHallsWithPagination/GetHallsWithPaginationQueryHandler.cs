using Cinema.Application.Common.Interfaces;
using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Queries.GetHallsWithPagination;

public class GetHallsWithPaginationQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetHallsWithPaginationQuery, Result<PaginatedList<HallDto>>>
{
    public async Task<Result<PaginatedList<HallDto>>> Handle(GetHallsWithPaginationQuery request, CancellationToken ct)
    {
        var query = context.Halls.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            query = query.Where(h => h.Name.Contains(request.SearchTerm));
        }

        if (request.IsActive.HasValue)
        {
            query = query.Where(h => h.IsActive == request.IsActive.Value);
        }
        
        var dtoQuery = query
            .OrderBy(h => h.Name)
            .ProjectToType<HallDto>(); 

        var pagedList = await PaginatedList<HallDto>.CreateAsync(dtoQuery, request.PageNumber, request.PageSize);
        
        return Result.Success(pagedList);
    }
}