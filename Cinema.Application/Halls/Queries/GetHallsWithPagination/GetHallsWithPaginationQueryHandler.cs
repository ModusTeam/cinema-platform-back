using Cinema.Application.Common.Interfaces;
using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Queries.GetHallsWithPagination;



public class GetHallsWithPaginationQueryHandler : IRequestHandler<GetHallsWithPaginationQuery, Result<PaginatedList<HallDto>>>
{
    private readonly IApplicationDbContext _context;

    public GetHallsWithPaginationQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<PaginatedList<HallDto>>> Handle(GetHallsWithPaginationQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Halls
            .AsNoTracking()
            .AsQueryable();
        
        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            query = query.Where(h => h.Name.Contains(request.SearchTerm));
        }

        if (request.IsActive.HasValue)
        {
            query = query.Where(h => h.IsActive == request.IsActive.Value);
        }

        query = query.OrderBy(h => h.Name);

        var dtoQuery = query.Select(h => new HallDto(
            h.Id.Value,
            h.Name,
            h.TotalCapacity,
            null, 
            h.Technologies.Select(ht => new TechnologyDto(
                ht.Technology!.Id.Value,
                ht.Technology.Name,
                ht.Technology.Type
            )).ToList()
        ));

        var pagedList = await PaginatedList<HallDto>.CreateAsync(
            dtoQuery, 
            request.PageNumber, 
            request.PageSize);

        return Result.Success(pagedList);
    }
}