using Cinema.Application.Common.Interfaces;
using Cinema.Application.Pricings.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Pricings.Queries;

public record GetPricingByIdQuery(Guid Id) : IRequest<Result<PricingDetailsDto>>;

public class GetPricingByIdQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetPricingByIdQuery, Result<PricingDetailsDto>>
{
    public async Task<Result<PricingDetailsDto>> Handle(GetPricingByIdQuery request, CancellationToken ct)
    {
        var pricingId = new EntityId<Pricing>(request.Id);

        var pricing = await context.Pricings
            .AsNoTracking()
            .Include(p => p.PricingItems)
            .ThenInclude(pi => pi.SeatType)
            .FirstOrDefaultAsync(p => p.Id == pricingId, ct);

        if (pricing == null)
        {
            return Result.Failure<PricingDetailsDto>(new Error("Pricing.NotFound", "Pricing not found"));
        }

        var config = TypeAdapterConfig.GlobalSettings.Fork(c => 
        {
            c.NewConfig<Pricing, PricingDetailsDto>()
                .Map(dest => dest.Items, src => src.PricingItems);
        });

        var dto = pricing.Adapt<PricingDetailsDto>(config);

        return Result.Success(dto);
    }
}