using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class PricingItem
{
    public EntityId<PricingItem> Id { get; }
    public decimal Price { get; private set; }
    public EntityId<Pricing> PricingId { get; private set; }
    public Pricing? Pricing { get; private set; }
    public EntityId<SeatType> SeatTypeId { get; private set; }
    public SeatType? SeatType { get; private set; }
    public DayOfWeek? DayOfWeek { get; private set; }
    public DateTime? StartTime { get; private set; }
    public DateTime? EndTime { get; private set; }

    private PricingItem(
        EntityId<PricingItem> id,
        decimal price,
        EntityId<Pricing> pricingId,
        EntityId<SeatType> seatTypeId,
        DayOfWeek? dayOfWeek,
        DateTime? startTime,
        DateTime? endTime)
    {
        Id = id;
        Price = price;
        PricingId = pricingId;
        SeatTypeId = seatTypeId;
        DayOfWeek = dayOfWeek;
        StartTime = startTime;
        EndTime = endTime;
    }

    public static PricingItem New(
        EntityId<PricingItem> id,
        decimal price,
        EntityId<Pricing> pricingId,
        EntityId<SeatType> seatTypeId,
        DayOfWeek? dayOfWeek,
        DateTime? startTime,
        DateTime? endTime)
        => new(id, price, pricingId, seatTypeId, dayOfWeek, startTime, endTime);
}