using Cinema.Application.Account.Commands.SetMyDateOfBirth;
using Cinema.Application.Users.Commands.ChangeUserRole;
using Cinema.Application.Users.Queries.GetAllUsers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[Authorize]
public class UsersController : ApiController
{
    /// <summary>
    /// Sets the authenticated user's date of birth (set-once). Publishes UserDateOfBirthSetIntegrationEvent to Loyalty service.
    /// </summary>
    [HttpPatch("me/date-of-birth")]
    [ProducesResponseType(typeof(SetMyDateOfBirthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMyDateOfBirth([FromBody] SetMyDateOfBirthCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        return HandleResult(await Mediator.Send(new GetAllUsersQuery()));
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("{id:guid}/role")]
    public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeRoleRequest request)
    {
        if (UserId == id)
        {
            return BadRequest("You cannot change your own role.");
        }

        var command = new ChangeUserRoleCommand(id, request.RoleName);
        return HandleResult(await Mediator.Send(command));
    }
}

public record ChangeRoleRequest(string RoleName);