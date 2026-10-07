using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Pricings.Commands.CreatePricing;

public record CreatePricingCommand(string Name) : IRequest<Result<Guid>>;

public class CreatePricingCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CreatePricingCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreatePricingCommand request, CancellationToken ct)
    {
        var pricingId = EntityId<Pricing>.New();
        var pricing = Pricing.New(pricingId, request.Name);

        context.Pricings.Add(pricing);
        await context.SaveChangesAsync(ct);

        return Result.Success(pricingId.Value);
    }
}
