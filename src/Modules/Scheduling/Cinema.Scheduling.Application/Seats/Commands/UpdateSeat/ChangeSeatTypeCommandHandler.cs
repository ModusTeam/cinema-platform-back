using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.UpdateSeat;

public class ChangeSeatTypeHandler : IRequestHandler<ChangeSeatTypeCommand, Result>
{
    private readonly IApplicationDbContext _context;

    public ChangeSeatTypeHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result> Handle(ChangeSeatTypeCommand request, CancellationToken cancellationToken)
    {
        var seatId = new EntityId<Seat>(request.SeatId);
        var newTypeId = new EntityId<SeatType>(request.NewSeatTypeId);

        var seat = await _context.Seats
            .FirstOrDefaultAsync(s => s.Id == seatId, cancellationToken);

        if (seat == null)
            return Result.Failure(new Error("Seat.NotFound", "Seat not found."));
        
        var typeExists = await _context.SeatTypes
            .AnyAsync(t => t.Id == newTypeId, cancellationToken);

        if (!typeExists)
            return Result.Failure(new Error("SeatType.NotFound", "Target seat type does not exist."));

        seat.ChangeType(newTypeId);

        await _context.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}