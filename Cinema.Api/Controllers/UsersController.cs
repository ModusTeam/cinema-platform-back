using Cinema.Application.Users.Commands.ChangeUserRole;
using Cinema.Application.Users.Queries.GetAllUsers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[Authorize(Roles = "Admin")]
public class UsersController : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        return HandleResult(await Mediator.Send(new GetAllUsersQuery()));
    }

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