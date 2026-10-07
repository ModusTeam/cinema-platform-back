using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using Mapster;
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
            .ProjectToType<HallLookupDto>()
            .ToListAsync(cancellationToken);

        return Result.Success(lookups);
    }
}