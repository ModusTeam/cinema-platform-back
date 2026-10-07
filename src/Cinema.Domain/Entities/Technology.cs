using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Technology
{
    public EntityId<Technology> Id { get; }
    public string Name { get; private set; }
    public string Type { get; private set; }

    private Technology(
        EntityId<Technology> id,
        string name,
        string type)
    {
        Id = id;
        Name = name;
        Type = type;
    }

    public static Technology New(EntityId<Technology> id, string name, string type)
        => new(id, name, type);
}