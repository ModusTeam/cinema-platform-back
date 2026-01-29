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
)
{
    public static SeatDto FromDomainModel(Seat seat)
        => new(
            seat.Id.Value,
            seat.RowLabel,
            seat.Number,
            seat.GridX,
            seat.GridY,
            seat.Status.ToString(),
            seat.SeatTypeId.Value,
            seat.SeatType?.Name ?? "Unknown"
        );
}

public record HallDto(
    Guid Id, 
    string Name, 
    int Capacity, 
    List<SeatDto>? Seats = null,
    List<TechnologyDto>? Technologies = null
)
{
    public static HallDto FromDomainModel(Hall hall)
        => new(
            hall.Id.Value, 
            hall.Name, 
            hall.TotalCapacity,
            hall.Seats?.Select(SeatDto.FromDomainModel).ToList(),
            hall.Technologies?
                .Select(ht => TechnologyDto.FromDomainModel(ht.Technology!))
                .ToList() ?? []
        );
}