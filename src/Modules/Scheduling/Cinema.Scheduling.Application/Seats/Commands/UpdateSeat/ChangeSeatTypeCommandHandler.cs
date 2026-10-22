using Cinema.Application.Common.Interfaces;
using Cinema.Application.Services;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.UpdateSeat;

public class ChangeSeatTypeHandler(IApplicationDbContext context, HallLayoutEditingService editingService)
    : IRequestHandler<ChangeSeatTypeCommand, Result>
{
    public async Task<Result> Handle(ChangeSeatTypeCommand request, CancellationToken cancellationToken)
    {
        var seatId = new EntityId<Seat>(request.SeatId);
        var newTypeId = new EntityId<SeatType>(request.NewSeatTypeId);

        var seat = await context.Seats.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seatId, cancellationToken);

        if (seat == null)
            return Result.Failure(new Error("Seat.NotFound", "Seat not found."));
        
        return await editingService.EditAsync(seat.HallId, async (hall, ct) =>
        {
            var target = hall.Seats.SingleOrDefault(s => s.Id == seatId);
            if (target is null)
                return Result.Failure(new Error("Seat.NotFound", "Seat not found."));
            bool typeExists = await context.SeatTypes.AnyAsync(t => t.Id == newTypeId, ct);
            if (!typeExists)
                return Result.Failure(new Error("SeatType.NotFound", "Target seat type does not exist."));
            target.ChangeType(newTypeId);
            return Result.Success();
        }, cancellationToken);
    }
}
