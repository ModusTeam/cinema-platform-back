using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
namespace Cinema.Infrastructure.Services;

public sealed record RefundClaim(Guid OperationId, Guid Token, bool FirstDispatch, RefundProviderRequest Request);

// Not registered in production. Each phase owns a fresh context; no Order/Ticket projections or network calls.
public sealed class RefundOperationStore(IDbContextFactory<ApplicationDbContext> contexts)
{
    public Task<Result<RefundOperation>> PersistApprovedAsync(RefundOperation candidate, CancellationToken ct)
        => Run(async db =>
        {
            if (candidate.State != RefundState.Approved || candidate.Attempts != 0)
                return Result.Failure<RefundOperation>(new Error("Refund.InvalidInstruction", "Expected an approved instruction"));
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            Order? order = await db.Orders.FromSqlInterpolated($"SELECT * FROM orders WHERE id={candidate.OrderId.Value} FOR UPDATE").SingleOrDefaultAsync(ct);
            RefundOperation? existing = await db.Set<RefundOperation>().SingleOrDefaultAsync(x => x.OrderId == candidate.OrderId, ct);
            if (existing is not null)
            {
                bool same = existing.UserId == candidate.UserId && existing.SessionId == candidate.SessionId &&
                    existing.ChargeReference == candidate.ChargeReference && existing.Amount == candidate.Amount && existing.Currency == candidate.Currency &&
                    existing.ActorId == candidate.ActorId && existing.Reason == candidate.Reason && existing.AdmissionEvidence == candidate.AdmissionEvidence &&
                    existing.PaidAtUtc == candidate.PaidAtUtc;
                return same ? Result.Success(existing) : Result.Failure<RefundOperation>(new Error("Refund.IdentityConflict", "Order already owns a refund instruction"));
            }
            DateTime now = await Now(db, ct);
            if (order is null || order.Status != Cinema.Domain.Enums.OrderStatus.Paid || order.PaidAmount <= 0 ||
                order.PaidAmount != candidate.Amount || order.PaymentTransactionId != candidate.ChargeReference ||
                order.PaidAt != candidate.PaidAtUtc || order.UserId != candidate.UserId || order.SessionId.Value != candidate.SessionId || candidate.ApprovedAtUtc > now)
                return Result.Failure<RefundOperation>(new Error("Refund.SourceMismatch", "Confirmed monetary source does not match instruction"));
            db.Add(candidate); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Result.Success(candidate);
        }, ct);

    public Task<RefundOperation?> GetAsync(Guid id, CancellationToken ct)
        => Run(db => db.Set<RefundOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct), ct);

    public Task<RefundClaim?> ClaimAsync(Guid id, CancellationToken ct)
        => Run<RefundClaim?>(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            RefundOperation? op = await db.Set<RefundOperation>().FromSqlInterpolated($"SELECT * FROM refund_operations WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (op is null) return null;
            DateTime now = await Now(db, ct);
            // Local authoritative success is not a new money attempt and must survive budget exhaustion.
            if (op.LastCode != RefundOperation.ConflictingProviderOutcomesCode && !(op.LeaseExpiresAtUtc > now) &&
                await db.Set<RefundProviderObservation>().AnyAsync(x => x.OperationId == id && x.Outcome == RefundState.Succeeded, ct))
            {
                if (op.State == RefundState.Declined || await db.Set<RefundProviderObservation>().AnyAsync(x => x.OperationId == id && x.Outcome == RefundState.Declined, ct))
                    op.FlagConflictingProviderOutcomes(now);
                else op.ConfirmObservedSuccess(now);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
            }
            bool first = op.DispatchStartedAtUtc is null;
            Guid token = Guid.NewGuid();
            bool claimed = op.TryClaim(token, now, TimeSpan.FromSeconds(30));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return claimed ? new(op.Id, token, first, new(op.Id, op.ProviderKey, op.ChargeReference, op.Amount, op.Currency)) : null;
        }, ct);

    // Durable even if the claim expires or a future projection cannot be applied.
    public Task ObserveAsync(RefundClaim claim, RefundProviderOutcome outcome, CancellationToken ct)
        => Run(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            RefundOperation op = await db.Set<RefundOperation>().FromSqlInterpolated($"SELECT * FROM refund_operations WHERE id={claim.OperationId} FOR UPDATE").SingleAsync(ct);
            DateTime now = await Now(db, ct);
            RefundProviderObservation incoming = RefundProviderObservation.Create(claim.OperationId, claim.Token, State(outcome.Kind), outcome.ProviderReference,
                outcome.EvidenceCode, outcome.ProviderOccurredAtUtc, now);
            RefundProviderObservation? prior = await db.Set<RefundProviderObservation>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == claim.OperationId && x.ClaimToken == claim.Token, ct);
            if (prior is not null)
            {
                if (prior.Outcome != incoming.Outcome || prior.ProviderReference != incoming.ProviderReference || prior.EvidenceCode != incoming.EvidenceCode || prior.ProviderOccurredAtUtc != incoming.ProviderOccurredAtUtc)
                    throw new InvalidOperationException("Conflicting observation for one provider claim");
            }
            else db.Add(incoming);
            // Serialize with resolution: evidence remains append-only; a stale worker cannot own the head.
            if (incoming.Outcome == RefundState.Succeeded && op.State == RefundState.Declined ||
                incoming.Outcome == RefundState.Declined && op.State == RefundState.Succeeded &&
                await db.Set<RefundProviderObservation>().AnyAsync(x => x.OperationId == claim.OperationId && x.Outcome == RefundState.Succeeded, ct))
                op.FlagConflictingProviderOutcomes(now);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
        }, ct);

    public Task<RefundProviderOutcome?> SuccessfulObservationAsync(Guid id, CancellationToken ct)
        => Run<RefundProviderOutcome?>(async db =>
        {
            RefundProviderObservation? observation = await db.Set<RefundProviderObservation>().AsNoTracking()
                .Where(x => x.OperationId == id && x.Outcome == RefundState.Succeeded).OrderBy(x => x.ObservedAtUtc).FirstOrDefaultAsync(ct);
            return observation is null ? null : new(RefundOutcomeKind.Succeeded, observation.ProviderReference, observation.EvidenceCode, observation.ProviderOccurredAtUtc);
        }, ct);

    public Task<bool> ResolveAsync(RefundClaim claim, RefundState outcome, string? code, CancellationToken ct)
        => Run(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            RefundOperation? op = await db.Set<RefundOperation>().FromSqlInterpolated($"SELECT * FROM refund_operations WHERE id={claim.OperationId} FOR UPDATE").SingleOrDefaultAsync(ct);
            DateTime now = await Now(db, ct);
            if (op is null || op.State != RefundState.Dispatching || op.LeaseToken != claim.Token || op.LeaseExpiresAtUtc <= now) return false;
            RefundState? conflictingOutcome = outcome switch { RefundState.Declined => RefundState.Succeeded, RefundState.Succeeded => RefundState.Declined, _ => null };
            if (conflictingOutcome is not null && await db.Set<RefundProviderObservation>().AnyAsync(x => x.OperationId == claim.OperationId && x.Outcome == conflictingOutcome, ct))
            {
                outcome = RefundState.ReconciliationRequired; code = RefundOperation.ConflictingProviderOutcomesCode;
            }
            op.Resolve(outcome, now, code); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
        }, ct);

    internal static RefundState State(RefundOutcomeKind kind) => kind switch
    {
        RefundOutcomeKind.Succeeded => RefundState.Succeeded,
        RefundOutcomeKind.Declined => RefundState.Declined,
        RefundOutcomeKind.Pending => RefundState.Pending,
        RefundOutcomeKind.Unknown => RefundState.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static Task<DateTime> Now(ApplicationDbContext db, CancellationToken ct)
        => db.Database.SqlQuery<DateTime>($"""SELECT clock_timestamp() AS "Value" """).SingleAsync(ct);
    private async Task<T> Run<T>(Func<ApplicationDbContext, Task<T>> action, CancellationToken ct)
    {
        if (ExecutionStrategy.Current is not null) throw new InvalidOperationException("Refund phase cannot be nested in an execution strategy");
        await using ApplicationDbContext db = await contexts.CreateDbContextAsync(ct);
        return await new RefundExecutionStrategy(db).ExecuteAsync(() => action(db));
    }
    private sealed class RefundExecutionStrategy(ApplicationDbContext db)
        : ExecutionStrategy(db.Database.GetService<ExecutionStrategyDependencies>(), 0, TimeSpan.Zero)
    { protected override bool ShouldRetryOn(Exception exception) => false; }
}
