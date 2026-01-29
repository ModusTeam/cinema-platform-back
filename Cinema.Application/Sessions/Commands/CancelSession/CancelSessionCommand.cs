using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Sessions.Commands.CancelSession;

public record CancelSessionCommand(Guid SessionId) : IRequest<Result>;