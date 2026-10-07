using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Technologies.Commands.CreateTechnology;

public class CreateTechnologyCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CreateTechnologyCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTechnologyCommand request, CancellationToken ct)
    {
        var exists = await context.Technologies
            .AnyAsync(t => t.Name == request.Name, ct);

        if (exists)
        {
            return Result.Failure<Guid>(new Error("Technology.Exists", $"Technology '{request.Name}' already exists."));
        }

        var id = EntityId<Technology>.New();
        
        var technology = Technology.New(id, request.Name, request.Type);

        context.Technologies.Add(technology);
        await context.SaveChangesAsync(ct);

        return Result.Success(id.Value);
    }
}