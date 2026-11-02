using System.Data.Common;
using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Services;

// Internal foundation only: no endpoint, DI registration, scheduler or money execution.
public sealed class OrdinaryRefundAdmission(RefundOperationStore store, ICurrentUserService currentUser)
{
    public Task<Result<RefundOperation>> ApproveAsync(Guid orderId, Guid requestId, CancellationToken ct)
    {
        if (currentUser.UserId is not { } actor || actor == Guid.Empty)
            return Task.FromResult(Result.Failure<RefundOperation>(new Error("Auth.Required", "Authentication required.")));
        if (orderId == Guid.Empty || requestId == Guid.Empty)
            return Task.FromResult(Result.Failure<RefundOperation>(new Error("Refund.InvalidRequest", "Order and request identity required.")));
        return store.AdmitOrdinaryAsync(orderId, requestId, actor, ct);
    }
}

public sealed partial class RefundOperationStore
{
    private const string OrdinaryReason = "Ordinary full refund under H-16";

    internal async Task<Result<RefundOperation>> AdmitOrdinaryAsync(Guid orderId, Guid requestId, Guid actor, CancellationToken ct)
    {
        try
        {
            return await Run(async db =>
            {
                var source = await db.Orders.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Id == new EntityId<Order>(orderId) && x.UserId == actor)
                    .Select(x => new { x.SessionId }).SingleOrDefaultAsync(ct);
                if (source is null) return Failure("Order.NotFound", "Order not found.");
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
                // Lifecycle: Hall UPDATE -> Session UPDATE; reservation: Session SHARE -> Seats.
                // We never lock Hall/Seats or wait for any external service.
                Session? session = await db.Sessions.FromSqlInterpolated($"SELECT * FROM sessions WHERE id={source.SessionId.Value} FOR SHARE")
                    .IgnoreQueryFilters().SingleOrDefaultAsync(ct);
                Order? order = await db.Orders.FromSqlInterpolated($"SELECT * FROM orders WHERE id={orderId} FOR UPDATE")
                    .IgnoreQueryFilters().SingleOrDefaultAsync(ct);
                if (order is null || order.UserId != actor) return Failure("Order.NotFound", "Order not found.");
                if (session is null || order.SessionId != source.SessionId) return Failure("Refund.SourceMismatch", "Order session requires reconciliation.");
                RefundOperation? existing = await db.Set<RefundOperation>().SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
                if (existing is not null)
                {
                    using JsonDocument evidence = JsonDocument.Parse(existing.AdmissionEvidence);
                    bool ordinary = evidence.RootElement.TryGetProperty("policy", out JsonElement policy) &&
                        policy.ValueKind == JsonValueKind.String && policy.GetString() == "ordinary-v1";
                    if (!ordinary || existing.ActorId != actor || existing.UserId != actor || existing.Reason != OrdinaryReason)
                        return Failure("Refund.IdentityConflict", "Order already owns a different approval.");
                    if (order.PaidCurrency is null || existing.Currency != order.PaidCurrency || existing.Amount != order.PaidAmount ||
                        existing.ChargeReference != order.PaymentTransactionId || existing.PaidAtUtc != order.PaidAt || existing.SessionId != order.SessionId.Value)
                        return Failure("Refund.SourceMismatch", "Approved payment source requires reconciliation.");
                    // Historical approval result: do not re-evaluate deadline or replace its original evidence.
                    return Result.Success(existing);
                }
                if (order.Status != OrderStatus.Paid || order.GoldPaymentOutcomeUnknown)
                    return Failure("Refund.PaymentReconciliationRequired", "A confirmed paid source is required.");
                if (order.GoldCheckoutState is not (GoldCheckoutState.None or GoldCheckoutState.Completed) || order.GoldRecoveryAfter is not null ||
                    (order.GoldCheckoutState == GoldCheckoutState.Completed
                        ? !order.GoldUseConfirmed || order.GoldTicketId is null
                        : order.GoldUseConfirmed || order.GoldTicketId is not null))
                    return Failure("Refund.GoldRecoveryRequired", "GOLD checkout recovery must finish first.");
                if (order.PaidAmount <= 0) return Failure("Refund.ZeroCash", "No monetary refund source; compensation requires a separate workflow.");
                if (order.PaidCurrency is null) return Failure("Refund.CurrencyReconciliationRequired", "Original charge currency is unknown.");
                if (order.PaidAt is null || string.IsNullOrWhiteSpace(order.PaymentTransactionId) ||
                    order.PointsUsed < 0 || order.TotalAmount - order.PointsUsed != order.PaidAmount)
                    return Failure("Refund.SourceMismatch", "Original monetary source requires reconciliation.");
                List<Ticket> tickets = await db.Tickets.FromSqlInterpolated($"SELECT * FROM tickets WHERE order_id={orderId} ORDER BY id FOR UPDATE")
                    .IgnoreQueryFilters().ToListAsync(ct);
                if (tickets.Count == 0 || tickets.Any(x => x.SessionId != order.SessionId || x.TicketStatus != TicketStatus.Valid) ||
                    tickets.Sum(x => x.PriceSnapshot) != order.TotalAmount)
                    return Failure("Refund.TicketReconciliationRequired", "Only matching valid paid tickets are eligible.");
                if (order.GoldCheckoutState == GoldCheckoutState.Completed
                    ? tickets.Count(x => x.IsGoldUpgraded) != 1 || !tickets.Any(x => x.IsGoldUpgraded && x.Id.Value == order.GoldTicketId)
                    : tickets.Any(x => x.IsGoldUpgraded))
                    return Failure("Refund.GoldRecoveryRequired", "GOLD ticket ownership requires reconciliation.");
                DateTime approval = await Now(db, ct);
                if (session.Status is not (SessionStatus.Scheduled or SessionStatus.OpenForSales or SessionStatus.SoldOut) ||
                    !RefundOperation.IsOrdinaryApprovalAllowed(session.StartTime, approval))
                    return Failure("Refund.TooLate", "Ordinary approval requires more than sixty minutes before screening start.");
                if (order.PaidAt > approval) return Failure("Refund.SourceMismatch", "Payment time requires reconciliation.");
                string snapshot = JsonSerializer.Serialize(new
                {
                    policy = "ordinary-v1", requestId, approvedAtUtc = approval, sessionStartTimeUtc = session.StartTime,
                    sessionStatus = session.Status.ToString(), orderStatus = order.Status.ToString(),
                    order.PaidAt, order.PaidAmount, order.PaidCurrency, order.TotalAmount, order.PointsUsed,
                    goldState = order.GoldCheckoutState.ToString(), order.GoldUseConfirmed, order.GoldPaymentOutcomeUnknown, order.GoldTicketId, order.GoldRecoveryAfter,
                    tickets = tickets.Select(x => new { id = x.Id.Value, sessionId = x.SessionId.Value, status = x.TicketStatus.ToString(), x.PriceSnapshot, x.IsGoldUpgraded })
                });
                RefundOperation operation = RefundOperation.Create(Guid.NewGuid(), orderId, order.UserId, order.SessionId.Value,
                    order.PaymentTransactionId, order.PaidAmount, order.PaidCurrency, order.PaidAt.Value, approval, actor, OrdinaryReason, snapshot);
                db.Add(operation); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return Result.Success(operation);
            }, ct);
        }
        catch (Exception exception) when (exception is DbException { SqlState: "55P03" } || exception.InnerException is DbException { SqlState: "55P03" })
        { return Failure("Refund.Busy", "Refund source is being modified. Retry with the same request identity."); }
    }

    private static Result<RefundOperation> Failure(string code, string message) => Result.Failure<RefundOperation>(new Error(code, message));
}
