using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Application.Common.Utils;
using Cinema.Application.Orders.Services;
using System.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace Cinema.Application.Orders.Commands.CancelOrder;

public record CancelOrderCommand(Guid OrderId) : IRequest<Result>;

public class CancelOrderCommandHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser,
    IPaymentService paymentService,
    ILoyaltyService loyaltyService,
    IPostCommitDomainEventDispatcher? postCommitDispatcher = null)
    : IRequestHandler<CancelOrderCommand, Result>
{
    public async Task<Result> Handle(CancelOrderCommand request, CancellationToken ct)
    {
        var orderId = new EntityId<Order>(request.OrderId);
        Guid? currentUserId = currentUser.UserId;
        bool isAdmin = currentUser.IsInRole("Admin");
        if (currentUserId is null && !isAdmin)
            return Result.Failure(new Error("Auth.Required", "User not authenticated"));

        var order = await context.Orders
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order == null)
            return Result.Failure(new Error("Order.NotFound", "Order not found."));

        if (order.UserId != currentUserId && !isAdmin)
            return Result.Failure(new Error("Order.NotFound", "Order not found."));

        if (order.GoldPaymentOutcomeUnknown)
            return Result.Failure(new Error("Order.PaymentReconciliationRequired", "Resolve the unknown payment outcome before cancellation."));

        if (order.GoldCheckoutState is GoldCheckoutState.UsePending or GoldCheckoutState.Consumed or GoldCheckoutState.CompensationPending)
            return Result.Failure(new Error("Order.GoldRecoveryRequired", "GOLD checkout compensation must finish before cancellation."));

        if (order.Status is OrderStatus.Cancelled or OrderStatus.Failed or OrderStatus.Expired)
            return Result.Failure(new Error("Order.AlreadyCancelled", "Order is already cancelled."));

        if (order.Status is OrderStatus.PaymentProcessing or OrderStatus.PaymentOutcomeUnknown)
            return Result.Failure(new Error("Order.InvalidState", "Payment outcome requires reconciliation before cancellation."));

        List<Ticket> tickets = await context.Tickets
            .IgnoreQueryFilters()
            .Where(t => t.OrderId == order.Id)
            .ToListAsync(ct);

        if (tickets.Count == 0)
            return Result.Failure(new Error("Order.InvalidState", "Order has no tickets to cancel."));

        TicketStatus expectedTicketStatus = order.Status == OrderStatus.Pending ? TicketStatus.Reserved : TicketStatus.Valid;
        if (tickets.Any(ticket => ticket.TicketStatus != expectedTicketStatus || ticket.SessionId != order.SessionId))
            return Result.Failure(new Error("Order.InvalidState", "Order tickets require reconciliation before cancellation."));

        if (order.Status == OrderStatus.Paid && order.PaidAmount > 0 &&
            string.IsNullOrEmpty(order.PaymentTransactionId))
            return Result.Failure(new Error("Order.InvalidState", "Paid order has no payment transaction to refund."));

        if (!isAdmin)
        {
            DateTime? sessionStart = await context.Sessions.IgnoreQueryFilters()
                .Where(session => session.Id == order.SessionId)
                .Select(session => (DateTime?)session.StartTime)
                .SingleOrDefaultAsync(ct);
            if (sessionStart is null)
                return Result.Failure(new Error("Order.InvalidState", "Order session is unavailable."));
            if (sessionStart <= DateTime.UtcNow.AddMinutes(10))
            {
                return Result.Failure(new Error("Order.TooLate", "Cancellation is only allowed up to 10 minutes before the session starts."));
            }
        }

        if (order.Status == OrderStatus.Paid)
        {
            Result<List<Ticket>> guard = await GuardPaidSourceAsync(order, currentUserId, isAdmin, ct);
            if (guard.IsFailure) return Result.Failure(guard.Error);
            tickets = guard.Value;
        }

        if (!string.IsNullOrEmpty(order.PaymentTransactionId) && order.Status == OrderStatus.Paid)
        {
            var refundResult = await paymentService.RefundPaymentAsync(order.PaymentTransactionId, ct);
            if (!refundResult.IsSuccess)
            {
                return Result.Failure(new Error("Payment.RefundFailed", "Payment provider rejected refund."));
            }
        }

        if (order.Status == OrderStatus.Paid && order.PointsUsed > 0)
        {
            string refundKey = DeterministicGuid.Create($"refund-{order.Id.Value}").ToString();
            var refundResult = await loyaltyService.RefundPointsAsync(order.UserId, order.PointsUsed,
                order.Id.Value, refundKey, ct);
            if (!refundResult.Success)
                return Result.Failure(new Error("Loyalty.RefundFailed", "Loyalty service rejected points refund."));
        }

        if (order.Status == OrderStatus.Paid && tickets.Any(ticket => ticket.IsGoldUpgraded))
        {
            var rollbackResult = await loyaltyService.RollbackGoldUpgradeAsync(order.UserId, order.Id.Value, ct);
            if (!rollbackResult.Success)
                return Result.Failure(new Error("Loyalty.GoldRollbackFailed", "Loyalty service rejected GOLD upgrade rollback."));
        }

        if (order.Status == OrderStatus.Pending && context.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory")
        {
            Result cancellation = await new CheckoutExecutionStrategy(context).ExecuteAsync(async () =>
            {
                await using var transaction = await context.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                int claimed = await context.Orders
                    .Where(candidate => candidate.Id == orderId && candidate.Status == OrderStatus.Pending)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, OrderStatus.Cancelled), ct);
                if (claimed == 0)
                {
                    await transaction.RollbackAsync(ct);
                    return Result.Failure(new Error("Order.InvalidState", "Order state changed during cancellation."));
                }

                order.MarkAsCancelled();
                foreach (Ticket ticket in tickets)
                    ticket.ReleaseReservation(order);
                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Result.Success();
            });
            if (cancellation.IsFailure)
                return cancellation;

            // Commit and transaction disposal finish before any external dispatch.
            if (postCommitDispatcher is not null)
                await postCommitDispatcher.DispatchAfterCommitAsync(ct);
            return cancellation;
        }

        order.MarkAsCancelled();
        foreach (Ticket ticket in tickets)
        {
            if (expectedTicketStatus == TicketStatus.Reserved)
                ticket.ReleaseReservation(order);
            else
                ticket.MarkAsRefunded();
        }

        await context.SaveChangesAsync(ct);

        return Result.Success();
    }

    // An authoritative preflight, NOT a durable cancellation claim. Once this transaction
    // commits, a later approval can still overlap the legacy network call. Activation blocker.
    private async Task<Result<List<Ticket>>> GuardPaidSourceAsync(Order order, Guid? userId, bool isAdmin, CancellationToken ct)
    {
        try
        {
            if (!context.Database.IsRelational())
            {
                if (await context.RefundOperations.AnyAsync(x => x.OrderId == order.Id, ct)) return RefundOwned();
                return Result.Success(await context.Tickets.IgnoreQueryFilters().Where(x => x.OrderId == order.Id).ToListAsync(ct));
            }
            if (context.Database.CurrentTransaction is not null) throw new InvalidOperationException("Fresh cancellation scope required.");
            return await new CheckoutExecutionStrategy(context).ExecuteAsync(async () =>
            {
                var sessionId = order.SessionId;
                await using var transaction = await context.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM sessions WHERE id={sessionId.Value} FOR SHARE", ct);
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM orders WHERE id={order.Id.Value} FOR UPDATE", ct);
                foreach (var entry in context.ChangeTracker.Entries<Order>().Where(x => x.Entity.Id == order.Id).ToList()) await entry.ReloadAsync(ct);
                if (order.UserId != userId && !isAdmin) return Invalid("Order.NotFound", "Order not found.");
                if (order.Status != OrderStatus.Paid || order.SessionId != sessionId) return Invalid("Order.InvalidState", "Order state changed during cancellation.");
                if (await context.RefundOperations.AsNoTracking().AnyAsync(x => x.OrderId == order.Id, ct)) return RefundOwned();
                List<Ticket> current = await context.Tickets.FromSqlInterpolated($"SELECT * FROM tickets WHERE order_id={order.Id.Value} ORDER BY id FOR UPDATE")
                    .IgnoreQueryFilters().ToListAsync(ct);
                foreach (var entry in context.ChangeTracker.Entries<Ticket>().Where(x => current.Contains(x.Entity)).ToList()) await entry.ReloadAsync(ct);
                if (order.GoldPaymentOutcomeUnknown) return Invalid("Order.PaymentReconciliationRequired", "Payment outcome requires reconciliation.");
                if (order.GoldCheckoutState is GoldCheckoutState.UsePending or GoldCheckoutState.Consumed or GoldCheckoutState.CompensationPending)
                    return Invalid("Order.GoldRecoveryRequired", "GOLD recovery must finish first.");
                if (current.Count == 0 || current.Any(x => x.OrderId != order.Id || x.SessionId != order.SessionId || x.TicketStatus != TicketStatus.Valid) ||
                    order.PaidAmount > 0 && string.IsNullOrEmpty(order.PaymentTransactionId))
                    return Invalid("Order.InvalidState", "Paid source requires reconciliation.");
                if (!isAdmin)
                {
                    DateTime? start = await context.Sessions.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == sessionId).Select(x => (DateTime?)x.StartTime).SingleOrDefaultAsync(ct);
                    if (start is null) return Invalid("Order.InvalidState", "Order session is unavailable.");
                    if (start <= DateTime.UtcNow.AddMinutes(10)) return Invalid("Order.TooLate", "Cancellation is only allowed up to 10 minutes before the session starts.");
                }
                await transaction.CommitAsync(ct);
                return Result.Success(current);
            });
        }
        catch (Exception exception) when (exception is DbException { SqlState: "55P03" } || exception.InnerException is DbException { SqlState: "55P03" })
        {
            context.ClearChangeTracker();
            return Invalid("Order.Busy", "Order lifecycle is being modified. Retry later.");
        }
        catch { context.ClearChangeTracker(); throw; }
    }

    private static Result<List<Ticket>> RefundOwned() => Invalid("Order.RefundLifecycleOwned", "Use the durable refund resolution workflow for this order.");
    private static Result<List<Ticket>> Invalid(string code, string message) => Result.Failure<List<Ticket>>(new Error(code, message));
}
