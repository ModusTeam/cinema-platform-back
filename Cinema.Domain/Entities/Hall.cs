using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Hall
{
    public EntityId<Hall> Id { get; }
    public string Name { get; private set; }
    public int TotalCapacity { get; private set; }

    public ICollection<Seat>? Seats { get; private set; } = [];
    public ICollection<Session>? Sessions { get; private set; } = [];
    public ICollection<HallTechnology>? Technologies { get; private set; } = [];

    private Hall(
        EntityId<Hall> id,
        string name,
        int totalCapacity)
    {
        Id = id;
        Name = name;
        TotalCapacity = totalCapacity;
    }

    public static Hall New(EntityId<Hall> id, string name, int totalCapacity, ICollection<HallTechnology> technologies)
    {
        return new(id, name, totalCapacity)
        {
            Technologies = technologies
        };
    }
}