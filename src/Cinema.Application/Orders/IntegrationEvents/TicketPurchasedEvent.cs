namespace Cinema.Application.Orders.IntegrationEvents;

public record TicketPurchasedEvent(
    Guid UserId,
    Guid OrderId,
    decimal TotalAmount,
    int TicketsCount,
    DateTime Timestamp
);
