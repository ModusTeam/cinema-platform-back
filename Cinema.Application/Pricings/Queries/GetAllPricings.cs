using Cinema.Application.Common.Interfaces;
using Cinema.Application.Pricings.Dtos;
using Cinema.Domain.Shared;
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
            .Include(p => p.PricingItems!)
            .ThenInclude(pi => pi.SeatType)
            .OrderBy(p => p.Name)
            .Select(p => new PricingDetailsDto(
                p.Id.Value,
                p.Name,
                p.PricingItems != null
                    ? p.PricingItems.Select(pi => new PricingItemDto(
                        pi.Id.Value,
                        pi.DayOfWeek ?? DayOfWeek.Sunday,
                        pi.SeatTypeId.Value,
                        pi.SeatType!.Name,
                        pi.Price
                    )).ToList()
                    : new List<PricingItemDto>()
            ))
            .ToListAsync(ct);

        return Result.Success(pricings);
    }
}