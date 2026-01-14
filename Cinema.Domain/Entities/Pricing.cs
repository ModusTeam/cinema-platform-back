using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Pricing : BaseEntity
{
    public required string Name { get; set; }
    
    public ICollection<PricingItem> PricingItems { get; set; } = new List<PricingItem>();
    public ICollection<SessionPricing> SessionPricings { get; set; } = new List<SessionPricing>();
}