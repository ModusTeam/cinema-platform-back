using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Orders.Queries.GetMyOrders;

public record GetMyOrdersQuery : IRequest<Result<OrderHistoryVm>>;

public class OrderHistoryVm
{
    public List<OrderDto> ActiveOrders { get; set; } = new();
    public List<OrderDto> PastOrders { get; set; } = new();
}