namespace Cinema.Application.Orders.Dtos;

public record MyOrdersVm(List<OrderDto> ActiveOrders, List<OrderDto> PastOrders);

public class OrderDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public int PointsUsed { get; set; }
    public List<TicketDto> Tickets { get; set; } = new();

    public decimal AmountPaid => TotalAmount - PointsUsed;
}