using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Queries.GetHallLookups;

public record HallLookupDto(Guid Id, string Name, int Capacity);

public record GetActiveHallsLookupQuery : IRequest<Result<List<HallLookupDto>>>;