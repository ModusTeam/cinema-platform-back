using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Sessions.Queries.GetSessionsByDateQuery;

public record GetSessionsByDateQuery(DateTime? Date) : IRequest<Result<List<SessionDto>>>;