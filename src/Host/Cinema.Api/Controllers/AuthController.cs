using Cinema.Application.Auth.Commands.LoginUser;
using Cinema.Application.Auth.Commands.RefreshToken;
using Cinema.Application.Auth.Commands.RegisterUser;
using Microsoft.AspNetCore.Mvc;
using Cinema.Application.Auth.Dtos;

namespace Cinema.Api.Controllers;

public class AuthController : ApiController
{
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterUserCommand command)
    {
        var result = await Mediator.Send(command);
        if (result.IsFailure) return HandleResult(result);
        
        return Ok(new { UserId = result.Value });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginUserCommand command)
    {
        var result = await Mediator.Send(command);
        if (result.IsFailure) return HandleResult(result);
        return Ok(result.Value);
    }
    
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        var command = new RefreshTokenCommand(request.Token);
        var result = await Mediator.Send(command);

        if (result.IsFailure) return HandleResult(result);

        return Ok(result.Value);
    }
}