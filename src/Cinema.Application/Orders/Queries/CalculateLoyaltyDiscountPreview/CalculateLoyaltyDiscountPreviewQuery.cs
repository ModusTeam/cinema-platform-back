using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Orders.Queries.CalculateLoyaltyDiscountPreview;

public record CalculateLoyaltyDiscountPreviewQuery(
    Guid SessionId,
    List<Guid> SeatIds) : IRequest<Result<LoyaltyDiscountPreviewDto>>;

public record LoyaltyDiscountPreviewDto(
    decimal OrderAmount,
    bool IsAllowed,
    int PointsToDeduct,
    decimal AmountToPay,
    string? Reason);
