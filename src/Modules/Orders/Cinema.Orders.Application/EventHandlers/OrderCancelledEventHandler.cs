using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Domain.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.EventHandlers;

public class OrderCancelledEventHandler(
    IApplicationDbContext context,
    ISeatLockingService seatLockingService,
    ITicketNotifier ticketNotifier,
    ILogger<OrderCancelledEventHandler> logger)
    : INotificationHandler<DomainEventNotification<OrderCancelledDomainEvent>>
{
    public async Task Handle(DomainEventNotification<OrderCancelledDomainEvent> notification, CancellationToken ct)
    {
        var domainEvent = notification.DomainEvent;
        var order = domainEvent.Order;

        logger.LogInformation("Domain Event: {DomainEvent} triggered for Order {OrderId}", domainEvent.GetType().Name, order.Id.Value);

        var tickets = await OrderEventTickets.LoadAsync(context, order.Id, ct);
        if (tickets.Count > 0)
        {
            var seatIds = tickets.Select(t => t.SeatId.Value).ToList();
            
            await seatLockingService.UnlockSeatsAsync(order.SessionId.Value, seatIds, order.UserId, ct);

            foreach (var ticket in tickets)
            {
                try
                {
                    await ticketNotifier.NotifySeatUnlockedAsync(ticket.SessionId.Value, ticket.SeatId.Value, ct);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to notify clients of seat unlock for seat {SeatId}", ticket.SeatId.Value);
                }
            }
        }
    }
}
