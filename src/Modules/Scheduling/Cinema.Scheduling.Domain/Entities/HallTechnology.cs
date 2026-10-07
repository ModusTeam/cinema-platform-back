using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class HallTechnology
{
    public EntityId<Hall> HallId { get; }
    public Hall? Hall { get; private set; }
    public EntityId<Technology> TechnologyId { get; }
    public Technology? Technology { get; private set; }

    private HallTechnology(
        EntityId<Hall> hallId,
        EntityId<Technology> technologyId)
    {
        HallId = hallId;
        TechnologyId = technologyId;
    }

    public static HallTechnology New(
        EntityId<Hall> hallId,
        EntityId<Technology> technologyId)
        => new(hallId, technologyId);
}