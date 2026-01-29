using Cinema.Application.Common.Interfaces;
using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Queries.GetHallById;

public class GetHallByIdQueryHandler : IRequestHandler<GetHallByIdQuery, Result<HallDto>>
{
    private readonly IApplicationDbContext _context;

    public GetHallByIdQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<HallDto>> Handle(GetHallByIdQuery request, CancellationToken cancellationToken)
    {
        var hallId = new EntityId<Hall>(request.Id);
        
        var hall = await _context.Halls
            .AsNoTracking()
            .AsSplitQuery()
            .Include(h => h.Seats)
            .ThenInclude(s => s.SeatType)
            .Include(h => h.Technologies)
            .ThenInclude(ht => ht.Technology)
            .FirstOrDefaultAsync(h => h.Id == hallId, cancellationToken);

        if (hall == null)
        {
            return Result.Failure<HallDto>(new Error("Hall.NotFound", "Hall not found"));
        }
        return Result.Success(HallDto.FromDomainModel(hall));
    }
}