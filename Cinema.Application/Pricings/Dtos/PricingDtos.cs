using Cinema.Domain.Enums;

namespace Cinema.Application.Pricings.Dtos;

public record PricingDto(Guid Id, string Name);

public record PricingItemDto(
    Guid Id,
    DayOfWeek DayOfWeek,
    Guid SeatTypeId,
    string SeatTypeName,
    decimal Price
);

public record PricingDetailsDto(
    Guid Id, 
    string Name, 
    List<PricingItemDto> Items
);

public record SetPricingRuleDto(
    DayOfWeek DayOfWeek,
    Guid SeatTypeId,
    decimal Price
);