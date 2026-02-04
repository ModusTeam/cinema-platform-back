namespace Cinema.Application.Orders.Dtos;

public record MyOrdersVm(List<OrderDto> ActiveOrders, List<OrderDto> PastOrders);
public record OrderDto(
    Guid Id,
    DateTime CreatedAt,
    decimal TotalAmount,
    string Status,
    List<TicketDto> Tickets
);