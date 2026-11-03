using System.Data;
using System.Data.Common;
using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Common.Utils;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Infrastructure.Services;

// Each database phase owns a real DI scope, including its context, interceptor,
// publisher and outbox. No failed context or deferred notifications are reused.
public sealed class PaidOrderCancellation(
    IServiceScopeFactory scopes, IPaymentService payment, ILoyaltyService loyalty) : IPaidOrderCancellation
{
    private const string Reconciliation = "Payment.RefundReconciliationRequired";

    public async Task<Result> CancelAsync(Guid orderId, Guid? actorId, bool isAdmin, CancellationToken ct)
    {
        if (actorId == Guid.Empty || actorId is null && !isAdmin) return Fail("Auth.Required", "User not authenticated");
        PaidCancellation? claim = null;
        Guid token = Guid.NewGuid();
        try
        {
            Result<PaidCancellation> admission;
            try { admission = await Claim(orderId, actorId, isAdmin, ct); }
            catch (Exception exception) when (!IsLockTimeout(exception))
            {
                // A lost commit acknowledgement is never replayed. Query the
                // stable owner from a new scope before deciding what happened.
                using var recovery = PersistenceTimeout();
                PaidCancellation? committed = await ReadAuthorized(orderId, actorId, isAdmin, recovery.Token);
                if (committed is null) return Fail("Order.CancellationPersistenceFailed", "Cancellation could not be durably claimed. Retry later.");
                admission = Result.Success(committed);
            }
            if (admission.IsFailure) return Result.Failure(admission.Error);
            claim = admission.Value;
            if (claim.FinalizedAtUtc is not null) return Result.Success();

            bool acquired = await UpdateAt(claim.Id, (op, now) => op.TryAcquire(token, now), ct);
            if (!acquired) return Fail("Order.Busy", "Cancellation is already being processed. Retry later.");
            claim = (await Read(claim.Id, ct))!;
            if (claim.MoneyState == CancellationStepState.Unknown)
                return await Release(claim.Id, token, Reconciliation);
            if (claim.MoneyState == CancellationStepState.Ready)
            {
                if (claim.PaidCurrency is null || string.IsNullOrWhiteSpace(claim.ChargeReference))
                    return await Release(claim.Id, token, "Order.CurrencyReconciliationRequired");
                ct.ThrowIfCancellationRequested();
                await Update(claim.Id, op => { op.BeginMoney(token); return true; }, ct);
                PaymentResult? result = null;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        // The adapter must honor its bounded cancellation token.
                        // Await its actual outcome: cancellation cannot undo money.
                        result = await payment.RefundPaymentAsync(claim.ChargeReference, timeout.Token);
                    }
                    catch (Exception) { /* No legacy lookup or definitive decline evidence. */ }
                }
                string? reference = result?.IsSuccess == true && !string.IsNullOrWhiteSpace(result.TransactionId)
                    ? result.TransactionId : null;
                using var persistence = PersistenceTimeout();
                try { await Update(claim.Id, op => { op.ObserveMoney(reference); return true; }, persistence.Token); }
                catch
                {
                    // An acknowledgement may have been lost after evidence committed.
                    // Never call the provider again to resolve this ambiguity.
                    using var recovery = PersistenceTimeout();
                    PaidCancellation? observed = await Read(claim.Id, recovery.Token);
                    if (reference is null || observed?.MoneyState != CancellationStepState.Succeeded || observed.RefundReference != reference) throw;
                }
                if (reference is null) return await Release(claim.Id, token, Reconciliation);
            }

            // These compensations alone are safe to replay: Loyalty persists the
            // exact points-command key/result and owns GOLD by (user, order).
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            claim = (await Read(claim.Id, completion.Token))!;
            if (claim.PointsState is CancellationStepState.Ready or CancellationStepState.Dispatching)
            {
                string key = DeterministicGuid.Create($"refund-{claim.OrderId.Value}").ToString();
                await Update(claim.Id, op => { op.BeginPoints(token); return true; }, completion.Token);
                bool success;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(completion.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    try { success = (await loyalty.RefundPointsAsync(claim.UserId, claim.PointsUsed, claim.OrderId.Value, key, timeout.Token)).Success; }
                    catch (Exception) { success = false; }
                }
                if (!success) return await Release(claim.Id, token, "Loyalty.RefundFailed");
                using var persistence = PersistenceTimeout();
                await Update(claim.Id, op => { op.CompletePoints(token); return true; }, persistence.Token);
            }
            if (claim.GoldState is CancellationStepState.Ready or CancellationStepState.Dispatching)
            {
                await Update(claim.Id, op => { op.BeginGold(token); return true; }, completion.Token);
                bool success;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(completion.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    try { success = (await loyalty.RollbackGoldUpgradeAsync(claim.UserId, claim.OrderId.Value, timeout.Token)).Success; }
                    catch (Exception) { success = false; }
                }
                if (!success) return await Release(claim.Id, token, "Loyalty.GoldRollbackFailed");
                using var persistence = PersistenceTimeout();
                await Update(claim.Id, op => { op.CompleteGold(token); return true; }, persistence.Token);
            }
            using var finalization = PersistenceTimeout();
            Result finalized = await Finalize(claim.Id, token, finalization.Token);
            if (finalized.IsFailure) await Release(claim.Id, token, finalized.Error.Code);
            return finalized;
        }
        catch (Exception exception)
        {
            string code = IsLockTimeout(exception) ? "Order.Busy" : "Order.CancellationReconciliationRequired";
            if (claim is not null)
            {
                using var recovery = PersistenceTimeout();
                try
                {
                    PaidCancellation? observed = await Read(claim.Id, recovery.Token);
                    if (observed?.FinalizedAtUtc is not null) return Result.Success();
                    await Update(claim.Id, op => { op.Release(token, code); return true; }, recovery.Token);
                }
                catch (Exception) { /* Durable intent still fences every other writer. */ }
            }
            return Fail(code, "Cancellation requires a fresh recovery attempt or reconciliation.");
        }
    }

    private Task<Result<PaidCancellation>> Claim(Guid id, Guid? actor, bool admin, CancellationToken ct)
        => Phase(async (db, _) =>
        {
            EntityId<Order> orderId = new(id);
            var source = await db.Orders.AsNoTracking().Where(x => x.Id == orderId && (admin || x.UserId == actor))
                .Select(x => new { x.SessionId }).SingleOrDefaultAsync(ct);
            if (source is null) return Reject("Order.NotFound", "Order not found.");
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await LockSource(db, source.SessionId.Value, id, ct);
            Order? order = await db.Orders.SingleOrDefaultAsync(x => x.Id == orderId, ct);
            if (order is null || !admin && order.UserId != actor) return Reject("Order.NotFound", "Order not found.");
            PaidCancellation? existing = await db.PaidCancellations.SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
            if (existing is not null) return Result.Success(existing);
            if (order.Status == OrderStatus.Cancelled) return Reject("Order.AlreadyCancelled", "Order is already cancelled.");
            if (await db.RefundOperations.AnyAsync(x => x.OrderId == orderId, ct))
                return Reject("Order.RefundLifecycleOwned", "Use the durable refund resolution workflow for this order.");
            if (order.Status != OrderStatus.Paid || order.SessionId != source.SessionId)
                return Reject("Order.InvalidState", "Order state changed during cancellation.");
            if (order.GoldPaymentOutcomeUnknown) return Reject("Order.PaymentReconciliationRequired", "Payment outcome requires reconciliation.");
            if (order.GoldCheckoutState is not (GoldCheckoutState.None or GoldCheckoutState.Completed) || order.GoldRecoveryAfter is not null)
                return Reject("Order.GoldRecoveryRequired", "GOLD checkout recovery must finish first.");
            List<Ticket> tickets = await LockedTickets(db, id, ct);
            if (tickets.Count == 0 || tickets.Any(x => x.SessionId != order.SessionId || x.TicketStatus != TicketStatus.Valid) ||
                order.PaidAmount < 0 || order.PointsUsed < 0 || order.TotalAmount - order.PointsUsed != order.PaidAmount ||
                tickets.Sum(x => x.PriceSnapshot) != order.TotalAmount || order.PaidAmount > 0 && string.IsNullOrWhiteSpace(order.PaymentTransactionId))
                return Reject("Order.InvalidState", "Paid source requires reconciliation.");
            if (order.GoldCheckoutState == GoldCheckoutState.Completed
                ? !order.GoldUseConfirmed || tickets.Count(x => x.IsGoldUpgraded) != 1 || !tickets.Any(x => x.IsGoldUpgraded && x.Id.Value == order.GoldTicketId)
                : order.GoldUseConfirmed || order.GoldTicketId is not null || tickets.Any(x => x.IsGoldUpgraded))
                return Reject("Order.GoldRecoveryRequired", "GOLD ticket ownership requires reconciliation.");
            Session? session = await db.Sessions.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == order.SessionId, ct);
            if (session is null) return Reject("Order.InvalidState", "Order session is unavailable.");
            DateTime now = await Now(db, ct);
            if (!admin && session.StartTime <= now.AddMinutes(10))
                return Reject("Order.TooLate", "Cancellation is only allowed up to 10 minutes before the session starts.");
            if (order.PaidAmount > 0 && order.PaidCurrency is null)
                return Reject("Order.CurrencyReconciliationRequired", "Original charge currency requires authoritative reconciliation.");
            string snapshot = JsonSerializer.Serialize(new
            {
                policy = "legacy-paid-cancellation-v1", session.StartTime, session.Status,
                order.TotalAmount, order.PaidAmount, order.PaidCurrency, order.PaidAt, order.PointsUsed,
                order.GoldCheckoutState, order.GoldTicketId, order.GoldUseConfirmed,
                order.GoldOriginalTicketPrice, order.GoldOriginalTotal, order.GoldOriginalPaid,
                tickets = tickets.Select(x => new { id = x.Id.Value, sessionId = x.SessionId.Value, x.TicketStatus, x.PriceSnapshot, x.IsGoldUpgraded })
            });
            PaidCancellation op = PaidCancellation.Claim(Guid.NewGuid(), order,
                actor, admin, order.GoldTicketId, snapshot, now);
            db.Add(op); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Result.Success(op);
        }, ct);

    private Task<Result> Finalize(Guid id, Guid token, CancellationToken ct)
        => Phase(async (db, dispatcher) =>
        {
            PaidCancellation source = await db.PaidCancellations.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
            {
                await LockSource(db, source.SessionId, source.OrderId.Value, ct);
                Order order = await db.Orders.SingleAsync(x => x.Id == source.OrderId, ct);
                List<Ticket> tickets = await LockedTickets(db, source.OrderId.Value, ct);
                PaidCancellation op = await db.PaidCancellations.FromSqlInterpolated($"SELECT * FROM paid_cancellations WHERE id={id} FOR UPDATE").SingleAsync(ct);
                if (op.FinalizedAtUtc is not null) return Result.Success();
                if (!op.Owns(token) || !op.CompensationComplete) return Fail("Order.CancellationReconciliationRequired", "Cancellation compensation is incomplete.");
                using JsonDocument snapshot = JsonDocument.Parse(op.SourceSnapshot);
                Guid[] originalTickets = snapshot.RootElement.GetProperty("tickets").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Order().ToArray();
                if (!MatchesOriginalSnapshot(order, tickets, snapshot.RootElement) ||
                    order.Status != OrderStatus.Paid || order.UserId != op.UserId || order.SessionId.Value != op.SessionId ||
                    order.PaidAmount != op.PaidAmount || order.PaidCurrency != op.PaidCurrency || order.PaidAt != op.PaidAtUtc ||
                    order.PaymentTransactionId != op.ChargeReference || order.PointsUsed != op.PointsUsed || order.GoldTicketId != op.GoldTicketId ||
                    !tickets.Select(x => x.Id.Value).Order().SequenceEqual(originalTickets) ||
                    tickets.Any(x => x.SessionId.Value != op.SessionId || x.TicketStatus != TicketStatus.Valid) ||
                    tickets.Sum(x => x.PriceSnapshot) != order.TotalAmount ||
                    tickets.Count(x => x.IsGoldUpgraded) != (op.GoldTicketId is null ? 0 : 1) ||
                    op.GoldTicketId is not null && !tickets.Any(x => x.Id.Value == op.GoldTicketId && x.IsGoldUpgraded) ||
                    await db.RefundOperations.AnyAsync(x => x.OrderId == op.OrderId, ct))
                    return Fail("Order.CancellationReconciliationRequired", "Claimed source changed; reconciliation required.");
                order.MarkAsCancelled();
                foreach (Ticket ticket in tickets) ticket.MarkAsRefunded();
                op.FinalizeCancellation(token, await Now(db, ct));
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            if (dispatcher is not null) await dispatcher.DispatchAfterCommitAsync(ct);
            return Result.Success();
        }, ct);

    private Task<bool> Update(Guid id, Func<PaidCancellation, bool> mutate, CancellationToken ct)
        => UpdateAt(id, (op, _) => mutate(op), ct);
    private Task<bool> UpdateAt(Guid id, Func<PaidCancellation, DateTime, bool> mutate, CancellationToken ct)
        => Phase(async (db, _) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            PaidCancellation op = await db.PaidCancellations.FromSqlInterpolated($"SELECT * FROM paid_cancellations WHERE id={id} FOR UPDATE").SingleAsync(ct);
            bool result = mutate(op, await Now(db, ct)); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
        }, ct);
    private static bool MatchesOriginalSnapshot(Order order, List<Ticket> tickets, JsonElement snapshot)
    {
        if (order.TotalAmount != snapshot.GetProperty("TotalAmount").GetDecimal() ||
            (int)order.GoldCheckoutState != snapshot.GetProperty("GoldCheckoutState").GetInt32() ||
            order.GoldUseConfirmed != snapshot.GetProperty("GoldUseConfirmed").GetBoolean() ||
            order.GoldOriginalTicketPrice != snapshot.GetProperty("GoldOriginalTicketPrice").GetDecimal() ||
            order.GoldOriginalTotal != snapshot.GetProperty("GoldOriginalTotal").GetDecimal() ||
            order.GoldOriginalPaid != snapshot.GetProperty("GoldOriginalPaid").GetDecimal() ||
            order.GoldPaymentOutcomeUnknown || order.GoldRecoveryAfter is not null) return false;
        Dictionary<Guid, JsonElement> original = snapshot.GetProperty("tickets").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
        return tickets.All(ticket => original.TryGetValue(ticket.Id.Value, out JsonElement prior) &&
            ticket.PriceSnapshot == prior.GetProperty("PriceSnapshot").GetDecimal() &&
            ticket.IsGoldUpgraded == prior.GetProperty("IsGoldUpgraded").GetBoolean());
    }
    private Task<PaidCancellation?> Read(Guid id, CancellationToken ct)
        => Phase((db, _) => db.PaidCancellations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct), ct);
    private Task<PaidCancellation?> ReadAuthorized(Guid id, Guid? actor, bool admin, CancellationToken ct)
        => Phase((db, _) => db.PaidCancellations.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == new EntityId<Order>(id) && (admin || x.UserId == actor), ct), ct);
    private async Task<Result> Release(Guid id, Guid token, string code)
    {
        using var persistence = PersistenceTimeout();
        await Update(id, op => { op.Release(token, code); return true; }, persistence.Token);
        return Fail(code, "Cancellation compensation requires recovery or reconciliation.");
    }
    private async Task<T> Phase<T>(Func<ApplicationDbContext, IPostCommitDomainEventDispatcher?, Task<T>> action, CancellationToken ct)
    {
        if (ExecutionStrategy.Current is { RetriesOnFailure: true }) throw new InvalidOperationException("Paid cancellation cannot run in a retried callback.");
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IPostCommitDomainEventDispatcher? dispatcher = scope.ServiceProvider.GetService<IPostCommitDomainEventDispatcher>();
        return await new PhaseStrategy(db).ExecuteAsync(() => action(db, dispatcher));
    }
    private static async Task LockSource(ApplicationDbContext db, Guid session, Guid order, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM sessions WHERE id={session} FOR SHARE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM orders WHERE id={order} FOR UPDATE", ct);
    }
    private static Task<List<Ticket>> LockedTickets(ApplicationDbContext db, Guid order, CancellationToken ct)
        => db.Tickets.FromSqlInterpolated($"SELECT * FROM tickets WHERE order_id={order} ORDER BY id FOR UPDATE").IgnoreQueryFilters().ToListAsync(ct);
    private static Task<DateTime> Now(ApplicationDbContext db, CancellationToken ct)
        => db.Database.SqlQuery<DateTime>($"""SELECT clock_timestamp() AS "Value" """).SingleAsync(ct);
    private static CancellationTokenSource PersistenceTimeout() => new(TimeSpan.FromSeconds(10));
    private static bool IsLockTimeout(Exception exception) => exception is DbException { SqlState: "55P03" } || exception.InnerException is DbException { SqlState: "55P03" };
    private static Result Fail(string code, string message) => Result.Failure(new Error(code, message));
    private static Result<PaidCancellation> Reject(string code, string message) => Result.Failure<PaidCancellation>(new Error(code, message));
    private sealed class PhaseStrategy(ApplicationDbContext db)
        : ExecutionStrategy(db.Database.GetService<ExecutionStrategyDependencies>(), 0, TimeSpan.Zero)
    { protected override bool ShouldRetryOn(Exception exception) => false; }
}
