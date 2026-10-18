using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
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
                    
                if (session == null || session.StartTime <= DateTime.UtcNow ||
                    session.Status is SessionStatus.Cancelled or SessionStatus.Ended or SessionStatus.SoldOut)
                    return false;
                
                var seatExists = await dbContext.Seats
                    .AsNoTracking()
                    .AnyAsync(s => s.Id == command.SeatId && s.HallId == session.HallId &&
                                   s.Status == SeatStatus.Active, ct);
                    
                if (!seatExists) return false;

                return !await dbContext.Tickets.AsNoTracking()
                    .AnyAsync(t => t.SessionId == session.Id && t.SeatId == new EntityId<Seat>(command.SeatId) &&
                                   (t.TicketStatus == TicketStatus.Reserved || t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used), ct);
            })
            .WithMessage("Invalid Session or Seat.");
    }
}
