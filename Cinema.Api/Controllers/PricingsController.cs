using Cinema.Application.Pricings.Commands.CreatePricing;
using Cinema.Application.Pricings.Commands.SetPricingRules;
using Cinema.Application.Pricings.Dtos;
using Cinema.Application.Pricings.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinema.Api.Controllers;

[Authorize(Roles = "Admin")]
public class PricingsController : ApiController
{
    [HttpGet]
    public async Task<ActionResult<List<PricingDetailsDto>>> GetAll()
    {
        var result = await Mediator.Send(new GetAllPricingsQuery());
        return HandleResult(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePricingCommand command)
    {
        var result = await Mediator.Send(command);
        return HandleResult(result);
    }
    
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await Mediator.Send(new GetPricingByIdQuery(id));
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPost("{id:guid}/rules")]
    public async Task<IActionResult> SetRules(Guid id, [FromBody] List<SetPricingRuleDto> rules)
    {
        var command = new SetPricingRulesCommand(id, rules);
        var result = await Mediator.Send(command);
        return HandleResult(result);
    }
}