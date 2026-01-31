using System.Security.Claims;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class ApiController : ControllerBase
{
    private ISender? _sender;
    
    protected ISender Mediator => _sender ??= HttpContext.RequestServices.GetRequiredService<ISender>();
    
    protected Guid UserId
    {
        get
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                               ?? User.FindFirst("sub")?.Value;
            
            return Guid.TryParse(userIdString, out var userId) ? userId : Guid.Empty;
        }
    }

    protected ActionResult HandleResult<T>(Result<T> result)
    {
        if (result.IsFailure) return HandleError(result.Error);
        return Ok(result.Value);
    }

    protected ActionResult HandleResult(Result result)
    {
        if (result.IsFailure) return HandleError(result.Error);
        return NoContent();
    }

    private ActionResult HandleError(Error error)
    {
        if (error.Code.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error.Code, error.Description });

        if (error.Code == "Seat.Locked" || error.Code == "Ticket.Expired") 
            return Conflict(new { error.Code, error.Description });

        return BadRequest(new { error.Code, error.Description });
    }
}