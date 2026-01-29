using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Queries.GetHallLookups;

public class GetHallLookupsQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetActiveHallsLookupQuery, Result<List<HallLookupDto>>>
{
    public async Task<Result<List<HallLookupDto>>> Handle(GetActiveHallsLookupQuery request, CancellationToken cancellationToken)
    {
        var lookups = await context.Halls
            .AsNoTracking()
            .Where(h => h.IsActive)
            .OrderBy(h => h.Name)
            .Select(h => new HallLookupDto(h.Id.Value, h.Name, h.TotalCapacity))
            .ToListAsync(cancellationToken);

        return Result.Success(lookups);
    }
}