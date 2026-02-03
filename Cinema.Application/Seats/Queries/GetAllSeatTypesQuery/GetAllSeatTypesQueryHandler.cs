using Cinema.Application.Common.Interfaces;
using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Queries.GetAllSeatTypesQuery;

public class GetAllSeatTypesQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetAllSeatTypesQuery, Result<List<SeatTypeDto>>>
{
    public async Task<Result<List<SeatTypeDto>>> Handle(GetAllSeatTypesQuery request, CancellationToken ct)
    {
        var types = await context.SeatTypes
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ProjectToType<SeatTypeDto>()
            .ToListAsync(ct);

        return Result.Success(types);
    }
}