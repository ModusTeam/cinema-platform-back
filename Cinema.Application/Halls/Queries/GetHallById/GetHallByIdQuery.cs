using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Queries.GetHallById;

public record GetHallByIdQuery(Guid Id) : IRequest<Result<HallDto>>;