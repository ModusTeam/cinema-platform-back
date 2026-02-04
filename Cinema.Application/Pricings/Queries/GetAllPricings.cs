using Cinema.Application.Common.Interfaces;
using Cinema.Application.Pricings.Dtos;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Pricings.Queries;

public record GetAllPricingsQuery : IRequest<Result<List<PricingDetailsDto>>>;

public class GetAllPricingsQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetAllPricingsQuery, Result<List<PricingDetailsDto>>>
{
    public async Task<Result<List<PricingDetailsDto>>> Handle(GetAllPricingsQuery request, CancellationToken ct)
    {
        var pricings = await context.Pricings
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ProjectToType<PricingDetailsDto>()
            .ToListAsync(ct);

        return Result.Success(pricings);
    }
}
