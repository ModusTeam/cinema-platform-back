using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Commands.DeleteHall;

public class DeleteHallCommandHandler(IApplicationDbContext context) : IRequestHandler<DeleteHallCommand, Result>
{
    public async Task<Result> Handle(DeleteHallCommand request, CancellationToken cancellationToken)
    {
        var hallId = new EntityId<Hall>(request.HallId);
        
        var hall = await context.Halls.FirstOrDefaultAsync(h => h.Id == hallId, cancellationToken);
        if (hall == null) return Result.Failure(new Error("Hall.NotFound", "Hall not found."));
        
        var hasActiveSessions = await context.Sessions
            .AnyAsync(s => s.HallId == hallId && s.EndTime > DateTime.UtcNow, cancellationToken);

        if (hasActiveSessions)
        {
            return Result.Failure(new Error("Hall.CannotDelete", 
                "Cannot delete hall with active or future sessions. Please cancel or reschedule sessions first."));
        }

        hall.Deactivate();

        await context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}