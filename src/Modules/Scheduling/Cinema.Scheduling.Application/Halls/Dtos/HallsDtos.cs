using Cinema.Domain.Entities;

namespace Cinema.Application.Halls.Dtos;

public record SeatTypeDto(Guid Id, string Name, string? Description)
{
    public static SeatTypeDto FromDomainModel(SeatType seatType)
        => new(seatType.Id.Value, seatType.Name, seatType.Description);
}

public record TechnologyDto(Guid Id, string Name, string Type)
{
    public static TechnologyDto FromDomainModel(Technology tech)
        => new(tech.Id.Value, tech.Name, tech.Type);
}

public record SeatDto(
    Guid Id,
    string Row,
    int Number,
    int GridX,
    int GridY,
    string Status,
    Guid SeatTypeId,
    string SeatTypeName
);

public record HallDto(
    Guid Id,
    string Name,
    int TotalCapacity,
    List<SeatDto>? Seats = null,
    List<TechnologyDto>? Technologies = null
);
