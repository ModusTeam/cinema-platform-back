using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Services;

public class HallLayoutEditingService(IApplicationDbContext context)
{
    public async Task<Result> EditAsync(EntityId<Hall> hallId,
        Func<Hall, CancellationToken, Task<Result>> edit, CancellationToken ct)
    {
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.BeginHallTransactionAsync(hallId, ct);
            try
            {
                var hall = await context.Halls.IgnoreQueryFilters()
                    .Include(h => h.Seats).SingleOrDefaultAsync(h => h.Id == hallId, ct);
                if (hall is null)
                    return Result.Failure(new Error("Hall.NotFound", "Hall not found."));
                if (!hall.IsActive)
                    return Result.Failure(new Error("Hall.Inactive", "An inactive hall cannot be edited."));

                // Ignore every global filter and status/date restriction, including inactive halls.
                bool hasHistory = await context.Sessions.IgnoreQueryFilters()
                    .AnyAsync(s => s.HallId == hallId, ct);
                bool hasSeatRecords = await context.Tickets.IgnoreQueryFilters()
                    .AnyAsync(t => t.Seat!.HallId == hallId, ct)
                    || await context.SeatLocks.IgnoreQueryFilters()
                        .AnyAsync(l => l.Seat!.HallId == hallId, ct);
                if (hasHistory || hasSeatRecords)
                    return Result.Failure(new Error("Hall.LayoutLocked",
                        "The layout and seat types cannot change after a hall has session history or seat-linked records."));

                Result result = await edit(hall, ct);
                if (result.IsFailure)
                    return result;

                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Result.Success();
            }
            catch
            {
                // A retry must reload the aggregate, not reuse a partially mutated tracked graph.
                context.ClearChangeTracker();
                throw;
            }
        });
    }
}
