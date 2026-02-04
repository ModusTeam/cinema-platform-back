using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.UpdateSeatStatus;

public record UpdateSeatStatusCommand(Guid SeatId, SeatStatus NewStatus) : IRequest<Result>;

public class UpdateSeatStatusCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<UpdateSeatStatusCommand, Result>
{
    public async Task<Result> Handle(UpdateSeatStatusCommand request, CancellationToken ct)
    {
        var seatId = new EntityId<Seat>(request.SeatId);
        
        var seat = await context.Seats
            .FirstOrDefaultAsync(s => s.Id == seatId, ct);

        if (seat == null)
            return Result.Failure(new Error("Seat.NotFound", "Seat not found."));

        if (seat.Status == request.NewStatus)
            return Result.Success();
        
        if (request.NewStatus != SeatStatus.Active)
        {
            var conflicts = await context.Tickets
                .AsNoTracking()
                .Where(t => 
                    t.SeatId == seatId && 
                    t.TicketStatus == TicketStatus.Valid &&
                    t.Session!.StartTime > DateTime.UtcNow)
                .Select(t => t.Session!.StartTime)
                .ToListAsync(ct);

            if (conflicts.Any())
            {
                var conflictDates = string.Join(", ", conflicts.Select(d => d.ToString("g")));
                return Result.Failure(new Error(
                    "Seat.Conflict", 
                    $"Cannot change seat status. Active tickets exist for sessions: {conflictDates}. Please cancel tickets first."
                ));
            }
        }
        
        seat.SetStatus(request.NewStatus);

        await context.SaveChangesAsync(ct);
        return Result.Success();
    }
}