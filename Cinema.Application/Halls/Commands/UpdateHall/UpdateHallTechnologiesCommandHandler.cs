using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Commands.UpdateHall;

public class UpdateHallTechnologiesCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<UpdateHallTechnologiesCommand, Result>
{
    public async Task<Result> Handle(UpdateHallTechnologiesCommand request, CancellationToken cancellationToken)
    {
        var hallId = new EntityId<Hall>(request.HallId);
        
        var hall = await context.Halls
            .Include(h => h.Technologies)
            .FirstOrDefaultAsync(h => h.Id == hallId, cancellationToken);

        if (hall == null) 
            return Result.Failure(new Error("Hall.NotFound", "Hall not found"));

        if (request.TechnologyIds.Any())
        {
            var techIdsToCheck = request.TechnologyIds.Select(id => new EntityId<Technology>(id)).ToList();
            
            var existingCount = await context.Technologies
                .CountAsync(t => techIdsToCheck.Contains(t.Id), cancellationToken);

            if (existingCount != techIdsToCheck.Count)
            {
                return Result.Failure(new Error("Technology.NotFound", "Invalid technology IDs provided."));
            }
        }

        var newTechIds = request.TechnologyIds.Select(id => new EntityId<Technology>(id));
        hall.UpdateTechnologies(newTechIds);

        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}