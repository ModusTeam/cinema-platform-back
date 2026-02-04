using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Domain.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.EventHandlers;

public class OrderPaidEventHandler(
    ITicketNotifier ticketNotifier,
    ISeatLockingService seatLockingService,
    ILogger<OrderPaidEventHandler> logger)
    : INotificationHandler<DomainEventNotification<OrderPaidEvent>>
{
    public async Task Handle(DomainEventNotification<OrderPaidEvent> notification, CancellationToken cancellationToken)
    {
        var domainEvent = notification.DomainEvent;
        var order = domainEvent.Order;


        logger.LogInformation("Event: Order {OrderId} paid. Starting background tasks.", order.Id.Value);
        
        var sessionId = order.SessionId.Value;

        foreach (var ticket in order.Tickets)
        {
            try
            {
                await seatLockingService.UnlockSeatAsync(
                    sessionId,
                    ticket.SeatId.Value,
                    order.UserId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to unlock seat {SeatId} in Redis.", ticket.SeatId.Value);
            }
        }
        
        try
        {
            await ticketNotifier.NotifyOrderCompleted(order.UserId, order.Id.Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send SignalR notification for order {OrderId}", order.Id.Value);
        }
    }
}