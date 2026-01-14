namespace Cinema.Domain.Entities;

public class SessionPricing
{
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;

    public Guid PricingId { get; set; }
    public Pricing Pricing { get; set; } = null!;
}