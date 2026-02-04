using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.Commands.CreateOrder;
using Cinema.Application.Orders.Dtos;
using Cinema.Application.Orders.Queries.GetMyOrders;
using Cinema.Application.Orders.Queries.GetUserOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[Authorize]
public class OrdersController : ApiController
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateOrderRequest request)
    {
        var command = new CreateOrderCommand(
            request.SessionId, 
            request.SeatIds, 
            request.PaymentToken, 
            Guid.NewGuid()
        );
        
        var result = await Mediator.Send(command);
        return HandleResult(result);
    }

    [HttpGet]
    public async Task<ActionResult<List<OrderDto>>> GetMyOrders()
    {
        var result = await Mediator.Send(new GetMyOrdersQuery());
        return HandleResult(result);
    }
    
    [Authorize(Roles = "Admin")]
    [HttpGet("user/{userId:guid}")]
    public async Task<IActionResult> GetOrdersByUserId(Guid userId)
    {
        var result = await Mediator.Send(new GetUserOrdersQuery(userId));
        return HandleResult(result);
    }
    
    [HttpPost("{id:guid}/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelMyOrder(Guid id)
    {
        var result = await Mediator.Send(new CancelOrderCommand(id));
        return HandleResult(result);
    }
}

public record CreateOrderRequest(Guid SessionId, List<Guid> SeatIds, string PaymentToken);