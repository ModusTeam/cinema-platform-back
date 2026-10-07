using Cinema.Application.Common.Interfaces;
using Cinema.Application.Pricings.Dtos;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Pricings.Queries;

public record GetPricingLookupsQuery : IRequest<Result<List<PricingLookupDto>>>;

public class GetPricingLookupsQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetPricingLookupsQuery, Result<List<PricingLookupDto>>>
{
    public async Task<Result<List<PricingLookupDto>>> Handle(GetPricingLookupsQuery request, CancellationToken ct)
    {
        var lookups = await context.Pricings
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ProjectToType<PricingLookupDto>()
            .ToListAsync(ct);

        return Result.Success(lookups);
    }
}