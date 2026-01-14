using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class PricingItem : BaseEntity
{
    public decimal Price { get; set; }

    public Guid PricingId { get; set; }
    public Pricing Pricing { get; set; } = null!;

    public Guid SeatTypeId { get; set; }
    public SeatType SeatType { get; set; } = null!;
}