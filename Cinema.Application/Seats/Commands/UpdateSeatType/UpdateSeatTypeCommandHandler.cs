using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.UpdateSeatType;

public class UpdateSeatTypeCommandHandler(IApplicationDbContext context)
    : IRequestHandler<UpdateSeatTypeCommand, Result>
{
    public async Task<Result> Handle(UpdateSeatTypeCommand request, CancellationToken ct)
    {
        var seatTypeId = new EntityId<SeatType>(request.Id);

        var seatType = await context.SeatTypes
            .FirstOrDefaultAsync(st => st.Id == seatTypeId, ct);

        if (seatType is null)
            return Result.Failure(new Error("SeatType.NotFound", $"Seat type with ID '{request.Id}' was not found."));

        var nameConflict = await context.SeatTypes
            .AnyAsync(st => st.Id != seatTypeId && st.Name == request.Name.Trim(), ct);

        if (nameConflict)
            return Result.Failure(new Error("SeatType.NameConflict", $"A seat type named '{request.Name}' already exists."));

        seatType.Update(request.Name, request.Description);

        await context.SaveChangesAsync(ct);

        return Result.Success();
    }
}
