using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Orders.Commands.CreateOrder;

public class CreateOrderCommand : IRequest<Result<Guid>>
{
    public Guid SessionId { get; set; }
    public List<Guid> SeatIds { get; set; } = new();
    public string PaymentToken { get; set; } = string.Empty;
    
    public bool UseLoyaltyPoints { get; set; } 
    public bool ApplyGoldUpgrade { get; set; }
}