using Cinema.Domain.Shared;
using FluentValidation;
using MediatR;

namespace Cinema.Application.Sessions.Commands.RescheduleSession;

public record RescheduleSessionCommand(
    Guid SessionId,
    DateTime NewStartTime
) : IRequest<Result>;
