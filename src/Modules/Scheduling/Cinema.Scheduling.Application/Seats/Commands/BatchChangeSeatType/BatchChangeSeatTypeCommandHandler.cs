using Cinema.Application.Common.Interfaces;
using Cinema.Application.Services;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.BatchChangeSeatType;

public class BatchChangeSeatTypeCommandHandler(IApplicationDbContext context, HallLayoutEditingService editingService)
    : IRequestHandler<BatchChangeSeatTypeCommand, Result>
{
    public async Task<Result> Handle(BatchChangeSeatTypeCommand request, CancellationToken ct)
    {
        return await editingService.EditAsync(new EntityId<Hall>(request.HallId), async (hall, token) =>
        {
            var newTypeId = new EntityId<SeatType>(request.NewSeatTypeId);
            bool typeExists = await context.SeatTypes.AnyAsync(t => t.Id == newTypeId, token);
            if (!typeExists)
                return Result.Failure(new Error("SeatType.NotFound", "Target seat type does not exist."));

            var seatIdsToCheck = request.SeatIds.Select(id => new EntityId<Seat>(id)).ToHashSet();
            var seats = hall.Seats.Where(s => seatIdsToCheck.Contains(s.Id)).ToList();

            if (seats.Count == 0)
                return Result.Failure(new Error("Seats.NotFound", "No valid seats found to update."));

            foreach (var seat in seats)
                seat.ChangeType(newTypeId);

            return Result.Success();
        }, ct);
    }
}
