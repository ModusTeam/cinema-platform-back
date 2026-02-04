using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Commands.CancelOrder;

public record CancelOrderCommand(Guid OrderId) : IRequest<Result>;

public class CancelOrderCommandHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser,
    IPaymentService paymentService) 
    : IRequestHandler<CancelOrderCommand, Result>
{
    public async Task<Result> Handle(CancelOrderCommand request, CancellationToken ct)
    {
        var orderId = new EntityId<Order>(request.OrderId);
        var currentUserId = currentUser.UserId;

        var order = await context.Orders
            .Include(o => o.Tickets!)
            .ThenInclude(t => t.Session)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order == null)
            return Result.Failure(new Error("Order.NotFound", "Order not found."));

        bool isAdmin = currentUser.IsInRole("Admin");
        
        if (order.UserId != currentUserId && !isAdmin)
        {
            return Result.Failure(new Error("Order.AccessDenied", "You can only cancel your own orders."));
        }

        if (order.Status == OrderStatus.Cancelled || order.Status == OrderStatus.Failed)
            return Result.Failure(new Error("Order.AlreadyCancelled", "Order is already cancelled."));

        if (!isAdmin)
        {
            var sessionStart = order.Tickets!.First().Session!.StartTime;
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

        order.MarkAsCancelled();
        
        foreach (var ticket in order.Tickets!)
        {
            ticket.MarkAsRefunded();
        }

        await context.SaveChangesAsync(ct);
        return Result.Success();
    }
}