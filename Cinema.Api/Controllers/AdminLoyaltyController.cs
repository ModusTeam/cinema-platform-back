using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediatR;
using System.Security.Claims;
using Cinema.Application.AdminLoyalty.Commands;
using Cinema.Application.AdminLoyalty.Queries;

namespace Cinema.Api.Controllers.Admin
{
    [ApiController]
    [Route("api/admin/loyalty")]
    [Authorize(Roles = "Admin")]
    public class AdminLoyaltyController : ControllerBase
    {
        private readonly ISender _mediator;

        public AdminLoyaltyController(ISender mediator)
        {
            _mediator = mediator;
        }

        [HttpGet("users/{userId}/balance")]
        public async Task<IActionResult> GetUserBalance(Guid userId)
        {
            var result = await _mediator.Send(new GetAdminUserBalanceQuery(userId));
            return Ok(result);
        }

        [HttpGet("users/{userId}/transactions")]
        public async Task<IActionResult> GetTransactionHistory(Guid userId, [FromQuery] int limit = 50, [FromQuery] int skip = 0)
        {
            var result = await _mediator.Send(new GetAdminTransactionHistoryQuery(userId, limit, skip));
            return Ok(result);
        }

        [HttpPost("modify-points")]
        public async Task<IActionResult> ModifyPoints([FromBody] ModifyPointsRequestDto dto)
        {
            var adminId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "UnknownAdmin";
            var command = new ModifyUserPointsCommand(dto.UserId, adminId, dto.Points, dto.Reason);
            
            var result = await _mediator.Send(command);
            return Ok(result);
        }
    }

    public record ModifyPointsRequestDto(Guid UserId, int Points, string Reason);
}