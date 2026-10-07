using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Sessions.Queries.GetSessionsWithPaginationQuery;

public record GetSessionsWithPaginationQuery(int PageNumber = 1, int PageSize = 10) 
    : IRequest<Result<PaginatedList<SessionDto>>>;