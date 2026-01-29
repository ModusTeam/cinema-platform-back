using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Queries.GetHallsWithPagination;

public record GetHallsWithPaginationQuery(
    int PageNumber = 1, 
    int PageSize = 10,
    string? SearchTerm = null,
    bool? IsActive = null 
) : IRequest<Result<PaginatedList<HallDto>>>;