using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Commands.CreateHall;

public record CreateHallCommand(
    string Name,
    int Rows,
    int SeatsPerRow,
    Guid SeatTypeId,
    List<Guid> TechnologyIds,
    List<CreateHallSeat>? Seats = null
) : IRequest<Result<Guid>>;

public record CreateHallSeat(int GridX, int GridY, Guid SeatTypeId);
