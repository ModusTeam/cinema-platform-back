using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Domain.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.EventHandlers;

public class OrderFailedEventHandler(
    ISeatLockingService seatLockingService,
    ILogger<OrderFailedEventHandler> logger)
    : INotificationHandler<DomainEventNotification<OrderFailedDomainEvent>>
{
    public async Task Handle(DomainEventNotification<OrderFailedDomainEvent> notification, CancellationToken ct)
    {
        var domainEvent = notification.DomainEvent;
        var order = domainEvent.Order;

        logger.LogInformation("Domain Event: {DomainEvent} triggered for Order {OrderId}", domainEvent.GetType().Name, order.Id.Value);

        if (order.Tickets != null && order.Tickets.Any())
        {
            var seatIds = order.Tickets.Select(t => t.SeatId.Value).ToList();
            await seatLockingService.UnlockSeatsAsync(order.SessionId.Value, seatIds, order.UserId, ct);
        }
    }
}
