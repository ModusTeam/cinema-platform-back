using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Sessions.Queries.GetSessionById;

public record GetSessionByIdQuery(Guid Id) : IRequest<Result<SessionDto>>;