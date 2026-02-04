using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Tickets.Queries.GetTicketDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[Authorize]
public class TicketsController : ApiController
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTicket(Guid id)
    {
        var result = await Mediator.Send(new GetTicketDetailsQuery(id));
        return HandleResult(result);
    }
    
    [HttpPost("{id:guid}/validate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ValidateTicket(Guid id)
    {
        var result = await Mediator.Send(new ValidateTicketCommand(id));
        return HandleResult(result);
    }
}