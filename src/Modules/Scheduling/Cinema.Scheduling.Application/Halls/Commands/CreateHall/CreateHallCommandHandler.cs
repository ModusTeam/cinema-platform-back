using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Services;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Commands.CreateHall;

public class CreateHallCommandHandler(
    IApplicationDbContext context,
    SeatLayoutService layoutService)
    : IRequestHandler<CreateHallCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHallCommand request, CancellationToken cancellationToken)
    {
        var nameExists = await context.Halls
            .AnyAsync(h => h.Name == request.Name && h.IsActive, cancellationToken);
            
        if (nameExists)
        {
            return Result.Failure<Guid>(new Error("Hall.NameExists", $"Hall with name '{request.Name}' already exists."));
        }
        
        var seatTypeId = new EntityId<SeatType>(request.SeatTypeId);
        List<EntityId<SeatType>> seatTypeIds = request.Seats is null
            ? [seatTypeId]
            : request.Seats.Select(s => new EntityId<SeatType>(s.SeatTypeId))
                .Append(seatTypeId).Distinct().ToList();
        int existingSeatTypeCount = await context.SeatTypes
            .CountAsync(x => seatTypeIds.Contains(x.Id), cancellationToken);
        if (existingSeatTypeCount != seatTypeIds.Count)
            return Result.Failure<Guid>(new Error("SeatType.NotFound", "One or more provided seat types do not exist."));

        if (request.TechnologyIds.Any())
        {
            var techIdsToCheck = request.TechnologyIds.Select(id => new EntityId<Technology>(id)).Distinct().ToList();
            
            var existingCount = await context.Technologies
                .CountAsync(t => techIdsToCheck.Contains(t.Id), cancellationToken);

            if (existingCount != techIdsToCheck.Count)
            {
                return Result.Failure<Guid>(new Error("Technology.NotFound", "One or more provided technologies do not exist."));
            }
        }

        var hallId = new EntityId<Hall>(Guid.NewGuid());
        var hall = Hall.Create(hallId, request.Name);
        
        var seats = request.Seats is null
            ? layoutService.GenerateRectangularGrid(request.Rows, request.SeatsPerRow, hallId, seatTypeId)
            : layoutService.GenerateSparseLayout(
                request.Seats.Select(s => (s.GridX, s.GridY, new EntityId<SeatType>(s.SeatTypeId))),
                hallId);
        
        hall.ApplyLayout(seats);

        if (request.TechnologyIds.Any())
        {
            var techIds = request.TechnologyIds.Select(g => new EntityId<Technology>(g));
            hall.UpdateTechnologies(techIds);
        }
        
        context.Halls.Add(hall);
        await context.SaveChangesAsync(cancellationToken);

        return Result.Success(hallId.Value);
    }
}
