using Cinema.Application.Common.Interfaces;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.LockSeat;

public class LockSeatCommandValidator : AbstractValidator<LockSeatCommand>
{
    public LockSeatCommandValidator(IApplicationDbContext dbContext)
    {
        RuleFor(x => x)
            .MustAsync(async (command, ct) =>
            {
                var session = await dbContext.Sessions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == command.SessionId, ct);
                    
                if (session == null) return false;
                
                var seatExists = await dbContext.Seats
                    .AsNoTracking()
                    .AnyAsync(s => s.Id == command.SeatId && s.HallId == session.HallId, ct);
                    
                return seatExists;
            })
            .WithMessage("Invalid Session or Seat.");
    }
}
