using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.CreateSeatType;

public record CreateSeatTypeCommand(string Name, string Description) : IRequest<Result<Guid>>;