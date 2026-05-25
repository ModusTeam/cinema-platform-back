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
        var statusCode = error.Code switch
        {
            "Seat.Locked" => StatusCodes.Status409Conflict,
            "Seat.AlreadyLocked" => StatusCodes.Status409Conflict,
            "Ticket.Expired" => StatusCodes.Status409Conflict,
            var code when code.Contains("NotFound", StringComparison.OrdinalIgnoreCase) => StatusCodes.Status404NotFound,
            var code when code.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest
        };

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = GetTitle(error),
            Detail = error.Description,
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
            Instance = HttpContext.Request.Path
        };

        problemDetails.Extensions.Add("code", error.Code);
        problemDetails.Extensions.Add("traceId", HttpContext.TraceIdentifier);

        return StatusCode(statusCode, problemDetails);
    }

    private static string GetTitle(Error error) =>
        error.Code switch
        {
            var code when code.Contains("NotFound") => "Resource Not Found",
            var code when code.Contains("Validation") => "Validation Error",
            "Seat.Locked" => "Conflict",
            "Seat.AlreadyLocked" => "Conflict",
            _ => "Bad Request"
        };
}