using Cinema.Application.Account.Commands.ChangePassword;
using Cinema.Application.Account.Commands.SetMyDateOfBirth;
using Cinema.Application.Account.Commands.UpdateProfile;
using Cinema.Application.Account.Queries.GetProfile;
using Cinema.Application.Account.Queries.GetLoyaltyProfile;
using Cinema.Application.Account.Queries.GetLoyaltyTransactions;
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
    
    /// <summary>
    /// Sets the authenticated user's date of birth. Used to unlock age-based bonuses.
    /// </summary>
    [HttpPatch("profile/date-of-birth")]
    [ProducesResponseType(typeof(SetMyDateOfBirthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMyDateOfBirth([FromBody] SetMyDateOfBirthCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }


    [HttpGet("loyalty/transactions")]
    public async Task<IActionResult> GetLoyaltyTransactions([FromQuery] int limit = 20, [FromQuery] int skip = 0)
    {
        return HandleResult(await Mediator.Send(new GetLoyaltyTransactionsQuery(limit, skip)));
    }
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }

    [HttpGet("loyalty")]
    public async Task<IActionResult> GetLoyaltyProfile()
    {
        return HandleResult(await Mediator.Send(new GetLoyaltyProfileQuery()));
    }
}

