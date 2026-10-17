using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Application.Common.Utils;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Commands.CancelOrder;

public record CancelOrderCommand(Guid OrderId) : IRequest<Result>;

public class CancelOrderCommandHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser,
    IPaymentService paymentService,
    ILoyaltyService loyaltyService)
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

        if (order.Status == OrderStatus.Cancelled || order.Status == OrderStatus.Failed)
            return Result.Failure(new Error("Order.AlreadyCancelled", "Order is already cancelled."));

        List<Ticket> tickets = await context.Tickets
            .IgnoreQueryFilters()
            .Where(t => t.OrderId == order.Id)
            .ToListAsync(ct);

        if (tickets.Count == 0)
            return Result.Failure(new Error("Order.InvalidState", "Order has no tickets to cancel."));

        if (tickets.Any(ticket => ticket.TicketStatus != TicketStatus.Valid || ticket.SessionId != order.SessionId))
            return Result.Failure(new Error("Order.InvalidState", "Order contains a ticket that cannot be refunded."));

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

        order.MarkAsCancelled();
        
        foreach (Ticket ticket in tickets)
        {
            ticket.MarkAsRefunded();
        }

        await context.SaveChangesAsync(ct);

        return Result.Success();
    }
}
