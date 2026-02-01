using Cinema.Application.Account.Commands.ChangePassword;
using Cinema.Application.Account.Commands.UpdateProfile;
using Cinema.Application.Account.Queries.GetProfile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[Authorize]
public class AccountController : ApiController
{
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        return HandleResult(await Mediator.Send(new GetProfileQuery()));
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }
}