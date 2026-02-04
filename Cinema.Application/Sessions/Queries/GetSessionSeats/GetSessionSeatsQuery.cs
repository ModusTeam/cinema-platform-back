using Cinema.Domain.Common;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Sessions.Queries.GetSessionSeats;

public record GetSessionSeatsQuery(Guid SessionId) : IRequest<Result<SessionSeatsVm>>;