using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Pricing
{
    public EntityId<Pricing> Id { get; }
    public string Name { get; private set; }

    public ICollection<PricingItem>? PricingItems { get; private set; } = [];

    private Pricing(
        EntityId<Pricing> id,
        string name)
    {
        Id = id;
        Name = name;
    }

    public static Pricing New(
        EntityId<Pricing> id,
        string name) => new(id, name);
}