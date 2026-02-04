using Cinema.Application.Common.Interfaces;
using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Queries.GetHallById;

public class GetHallByIdQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetHallByIdQuery, Result<HallDto>>
{
    public async Task<Result<HallDto>> Handle(GetHallByIdQuery request, CancellationToken ct)
    {
        var hallId = new EntityId<Hall>(request.Id);
        
        var hall = await context.Halls
            .AsNoTracking()
            .AsSplitQuery()
            .Include(h => h.Seats).ThenInclude(s => s.SeatType) 
            .Include(h => h.Technologies).ThenInclude(ht => ht.Technology)
            .FirstOrDefaultAsync(h => h.Id == hallId, ct);

        if (hall == null)
        {
            return Result.Failure<HallDto>(new Error("Hall.NotFound", "Hall not found"));
        }

        var config = TypeAdapterConfig.GlobalSettings.Fork(c => 
        {
            c.NewConfig<Hall, HallDto>()
                .Map(dest => dest.Seats, src => src.Seats)
                .Map(dest => dest.Technologies, src => src.Technologies.Select(t => t.Technology));
        });
        
        var hallDto = hall.Adapt<HallDto>(config);

        return Result.Success(hallDto);
    }
}