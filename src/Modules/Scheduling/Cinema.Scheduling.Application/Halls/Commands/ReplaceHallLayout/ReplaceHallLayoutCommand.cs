using Cinema.Application.Halls.Commands.CreateHall;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Commands.ReplaceHallLayout;

public record ReplaceHallLayoutCommand(Guid HallId, int Rows, int SeatsPerRow,
    List<CreateHallSeat> Seats) : IRequest<Result>;

public record ReplaceHallLayoutRequest(int Rows, int SeatsPerRow, List<CreateHallSeat> Seats);
