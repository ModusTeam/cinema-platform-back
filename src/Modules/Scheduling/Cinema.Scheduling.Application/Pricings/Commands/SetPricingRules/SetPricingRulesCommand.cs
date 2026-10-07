using Cinema.Application.Common.Interfaces;
using Cinema.Application.Pricings.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Pricings.Commands.SetPricingRules;

public record SetPricingRulesCommand(Guid PricingId, List<SetPricingRuleDto> Rules) : IRequest<Result>;

public class SetPricingRulesCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<SetPricingRulesCommand, Result>
{
    public async Task<Result> Handle(SetPricingRulesCommand request, CancellationToken ct)
    {
        var pricingId = new EntityId<Pricing>(request.PricingId);

        var pricing = await context.Pricings
            .Include(p => p.PricingItems)
            .FirstOrDefaultAsync(p => p.Id == pricingId, ct);

        if (pricing == null)
            return Result.Failure(new Error("Pricing.NotFound", "Pricing policy not found"));

        foreach (var rule in request.Rules)
        {
            var seatTypeId = new EntityId<SeatType>(rule.SeatTypeId);

            var existingItem = pricing.PricingItems?.FirstOrDefault(pi => 
                pi.DayOfWeek == rule.DayOfWeek && 
                pi.SeatTypeId == seatTypeId);

            if (existingItem != null)
            {
                existingItem.UpdatePrice(rule.Price); 
            }
            else
            {
                var newItem = PricingItem.New(
                    EntityId<PricingItem>.New(),
                    rule.Price,
                    pricingId,
                    seatTypeId,
                    rule.DayOfWeek,
                    null, null
                );
                context.PricingItems.Add(newItem);
            }
        }

        await context.SaveChangesAsync(ct);
        return Result.Success();
    }
}
