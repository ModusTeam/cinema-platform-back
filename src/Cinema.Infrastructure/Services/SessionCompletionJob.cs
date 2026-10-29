using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Cinema.Infrastructure.Services;
// Singleton cursor is only a fairness hint. Durable source state drives restart recovery.
public sealed class SessionCompletionJob(IServiceScopeFactory scopes, ILogger<SessionCompletionJob> logger)
{
    private DateTime? lastEnd;
    private Guid lastId;
    private readonly SemaphoreSlim gate = new(1);

    [AutomaticRetry(Attempts = 0)]
    public async Task Process(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            await using AsyncServiceScope read = scopes.CreateAsyncScope();
            ApplicationDbContext db = read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Stable statement cutoff and keyset; no OFFSET, no dependence on Hall.IsActive.
            var candidates = await db.Sessions.FromSqlInterpolated($"""
                SELECT * FROM sessions WHERE end_time <= statement_timestamp() AND status IN (0,1,2)
                AND ({lastEnd}::timestamptz IS NULL OR (end_time,id) > ({lastEnd}::timestamptz,{lastId}))
                ORDER BY end_time,id LIMIT 50
                """).IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
            if (candidates.Count == 0) { lastEnd = null; lastId = Guid.Empty; return; }
            foreach (Session session in candidates)
            {
                ct.ThrowIfCancellationRequested();
                lastEnd = session.EndTime;
                lastId = session.Id.Value; // Advance even on failure; wrap next empty tick.
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SessionCompletionService>().CompleteAsync(session.Id.Value, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { logger.LogError("Session completion failed: {SessionId} {Code}", session.Id.Value, "SESSION_COMPLETION_FAILED"); }
            }
        }
        finally { gate.Release(); }
    }
}
