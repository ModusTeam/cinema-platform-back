using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Commands.UpdateHall;

public class UpdateHallCommandHandler(IApplicationDbContext context) : IRequestHandler<UpdateHallCommand, Result>
{
    public async Task<Result> Handle(UpdateHallCommand request, CancellationToken cancellationToken)
    {
        var hallId = new EntityId<Hall>(request.HallId);
        
        var hall = await context.Halls
            .FirstOrDefaultAsync(h => h.Id == hallId, cancellationToken);

        if (hall == null)
            return Result.Failure(new Error("Hall.NotFound", "Hall not found."));
        
        var nameTaken = await context.Halls
            .AnyAsync(h => h.Name == request.Name && h.Id != hallId && h.IsActive, cancellationToken);

        if (nameTaken)
            return Result.Failure(new Error("Hall.NameExists", "Another hall with this name already exists."));

        try
        {
            hall.UpdateDetails(request.Name);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            return Result.Failure(new Error("Hall.Validation", ex.Message));
        }

        return Result.Success();
    }
}