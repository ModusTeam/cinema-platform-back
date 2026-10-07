using Cinema.Domain.Entities;

namespace Cinema.Application.Technologies.Dtos;

public record TechnologyDto(Guid Id, string Name, string Type)
{
    public static TechnologyDto FromDomain(Technology technology)
        => new(technology.Id.Value, technology.Name, technology.Type);
}