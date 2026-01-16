using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class SeatType
{
    public EntityId<SeatType> Id { get; }
    public string Name { get; private set; }
    public string? Description { get; private set; }

    private SeatType(EntityId<SeatType> id, string name, string? description)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public static SeatType New(EntityId<SeatType> id, string name, string? description)
        => new(id, name, description);
}