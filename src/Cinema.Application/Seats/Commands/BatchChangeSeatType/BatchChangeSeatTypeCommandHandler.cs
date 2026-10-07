using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.BatchChangeSeatType;

public class BatchChangeSeatTypeCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<BatchChangeSeatTypeCommand, Result>
{
    public async Task<Result> Handle(BatchChangeSeatTypeCommand request, CancellationToken ct)
    {
        var newTypeId = new EntityId<SeatType>(request.NewSeatTypeId);
        var typeExists = await context.SeatTypes.AnyAsync(t => t.Id == newTypeId, ct);
        if (!typeExists)
            return Result.Failure(new Error("SeatType.NotFound", "Target seat type does not exist."));
        
        var seatIdsToCheck = request.SeatIds.Select(id => new EntityId<Seat>(id)).ToList();
        
        var seats = await context.Seats
            .Where(s => seatIdsToCheck.Contains(s.Id) && s.HallId == new EntityId<Hall>(request.HallId))
            .ToListAsync(ct);

        if (seats.Count == 0)
            return Result.Failure(new Error("Seats.NotFound", "No valid seats found to update."));

        foreach (var seat in seats)
        {
            seat.ChangeType(newTypeId);
        }

        await context.SaveChangesAsync(ct);

        return Result.Success();
    }
}