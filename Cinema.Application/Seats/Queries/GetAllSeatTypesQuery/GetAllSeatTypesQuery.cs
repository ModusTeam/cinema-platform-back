using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Queries.GetAllSeatTypesQuery;

public record GetAllSeatTypesQuery : IRequest<Result<List<SeatTypeDto>>>;