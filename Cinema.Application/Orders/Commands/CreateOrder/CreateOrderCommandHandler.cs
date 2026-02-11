using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Events;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.Commands.CreateOrder;

public class CreateOrderCommandHandler(
    IOrderReservationService reservationService,
    ICurrentUserService currentUser,
    ISeatLockingService seatLockingService,
    IPaymentService paymentService,
    IApplicationDbContext context,
    IPublisher publisher,
    ILogger<CreateOrderCommandHandler> logger) : IRequestHandler<CreateOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateOrderCommand request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) return Result.Failure<Guid>(new Error("Auth.Required", "User not authenticated"));
        
        foreach (var seatId in request.SeatIds)
        {
            if (!await seatLockingService.ValidateAndExtendLockAsync(request.SessionId, seatId, userId.Value, ct))
            {
                 return Result.Failure<Guid>(new Error("Order.LockExpired", $"Lock expired for seat {seatId}."));
            }
        }
        
        var reservationResult = await reservationService.ReserveOrderAsync(userId.Value, request.SessionId, request.SeatIds, ct);
        
        if (reservationResult.IsFailure)
            return reservationResult;

        var orderId = reservationResult.Value;
        
        var orderAmount = await context.Orders
            .Where(o => o.Id == new EntityId<Order>(orderId))
            .Select(o => o.TotalAmount)
            .FirstOrDefaultAsync(ct);
        
        PaymentResult paymentResult;
        try
        {
            paymentResult = await paymentService.ProcessPaymentAsync(orderAmount, "UAH", request.PaymentToken, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Payment crash for Order {OrderId}", orderId);
            paymentResult = PaymentResult.Failure("Payment system error");
        }
        
        if (paymentResult.IsSuccess)
        {
            return await ConfirmOrderAsync(orderId, paymentResult.TransactionId!, userId.Value, request.SessionId, request.SeatIds, ct);
        }
        else
        {
            return await FailOrderAsync(orderId, paymentResult.ErrorMessage!, request.SessionId, request.SeatIds, userId.Value, ct);
        }
    }
    
    private async Task<Result<Guid>> ConfirmOrderAsync(Guid orderId, string transactionId, Guid userId, Guid sessionId, List<Guid> seatIds, CancellationToken ct)
    {
        var order = await context.Orders.Include(o => o.Tickets).FirstOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId), ct);
        if (order == null) return Result.Failure<Guid>(new Error("Order.NotFound", "Order not found"));

        order.MarkAsPaid(transactionId);
        await context.SaveChangesAsync(ct);
        
        await publisher.Publish(new OrderPaidEvent(order), ct);

        _ = Task.Run(async () => 
        {
            foreach (var seatId in seatIds)
            {
                await seatLockingService.UnlockSeatAsync(sessionId, seatId, userId, default);
            }
        });
        
        return Result.Success(order.Id.Value);
    }

    private async Task<Result<Guid>> FailOrderAsync(Guid orderId, string reason, Guid sessionId, List<Guid> seatIds, Guid userId, CancellationToken ct)
    {
        var order = await context.Orders.FindAsync([new EntityId<Order>(orderId)], ct);
        if (order != null)
        {
            order.MarkAsFailed();
            await context.SaveChangesAsync(ct);
        }
        
        foreach(var seatId in seatIds) 
            await seatLockingService.UnlockSeatAsync(sessionId, seatId, userId, ct);

        return Result.Failure<Guid>(new Error("Payment.Failed", reason));
    }
}