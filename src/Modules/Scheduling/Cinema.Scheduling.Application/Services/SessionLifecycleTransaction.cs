using System.Data.Common;
using Cinema.Domain.Shared;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
namespace Cinema.Application.Services;
// Hall -> Session. Reservation takes Session SHARE -> Seats, never Hall UPDATE.
// Lost commit acknowledgement requires fresh-scope verification, not callback replay.
public static class SessionLifecycleTransaction
{
    public static async Task<Result> ExecuteCommandAsync(IApplicationDbContext context, Guid id,
        Func<Session?, CancellationToken, Task<Result>> operation, CancellationToken ct)
    {
        try { return await ExecuteAsync(context, id, operation, ct); }
        catch (Exception exception) when (
            exception is DbException { SqlState: "55P03" } ||
            exception.InnerException is DbException { SqlState: "55P03" })
        {
            // PostgreSQL lock_not_available, including an EF-wrapped SaveChanges error.
            // The transaction has already been disposed/rolled back at this boundary.
            return Result.Failure(new Error("Session.Busy", "Session is being modified. Retry later."));
        }
    }

    public static async Task<T> ExecuteAsync<T>(IApplicationDbContext context, Guid id,
        Func<Session?, CancellationToken, Task<T>> operation, CancellationToken ct, bool includeInactive = false)
    {
        if (!context.Database.IsRelational())
            return await operation(await context.Sessions.FirstOrDefaultAsync(s => s.Id == new EntityId<Session>(id), ct), ct);
        if (context.Database.CurrentTransaction is not null || ExecutionStrategy.Current is not null)
            throw new InvalidOperationException("Fresh lifecycle scope required");
        try
        {
            return await new LifecycleStrategy(context).ExecuteAsync(async () =>
            {
                await using var tx = await context.Database.BeginTransactionAsync(ct);
                await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
                var hall = await context.Sessions.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.Id == new EntityId<Session>(id)).Select(s => s.HallId).SingleOrDefaultAsync(ct);
                if (hall != default)
                    await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM halls WHERE id = {hall.Value} FOR UPDATE", ct);
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM sessions WHERE id = {id} FOR UPDATE", ct);
                var tracked = context.ChangeTracker.Entries<Session>().SingleOrDefault(s => s.Entity.Id.Value == id);
                if (tracked is not null) await tracked.ReloadAsync(ct);
                var query = includeInactive ? context.Sessions.IgnoreQueryFilters() : context.Sessions;
                Session? session = await query.SingleOrDefaultAsync(s => s.Id == new EntityId<Session>(id), ct);
                T result = await operation(session, ct);
                await tx.CommitAsync(ct);
                return result;
            });
        }
        catch { context.ClearChangeTracker(); throw; }
    }
    private sealed class LifecycleStrategy(IApplicationDbContext db)
        : ExecutionStrategy(db.Database.GetService<ExecutionStrategyDependencies>(), 0, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
