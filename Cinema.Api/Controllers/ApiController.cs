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
    
    protected ActionResult HandleResult<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            return HandleError(result.Error);
        }

        return Ok(result.Value);
    }

    protected ActionResult HandleResult(Result result)
    {
        if (result.IsFailure)
        {
            return HandleError(result.Error);
        }

        return NoContent();
    }

    private ActionResult HandleError(Error error)
    {
        if (error.Code.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error.Code, error.Description });
        }

        return BadRequest(new { error.Code, error.Description });
    }
}