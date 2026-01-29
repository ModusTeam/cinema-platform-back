using Cinema.Application.Common.Interfaces;
using Cinema.Application.Services;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.RescheduleSession;

public class RescheduleSessionCommandHandler(
    SessionSchedulingService schedulingService,
    IApplicationDbContext context) 
    : IRequestHandler<RescheduleSessionCommand, Result>
{
    public async Task<Result> Handle(RescheduleSessionCommand request, CancellationToken ct)
    {
        var sessionId = new EntityId<Session>(request.SessionId);
        
        var session = await context.Sessions
            .Include(s => s.Tickets)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session == null)
        {
            return Result.Failure(new Error("Session.NotFound", "Session not found."));
        }
        
        var hasActiveTickets = session.Tickets.Any(t => 
            t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used);

        if (hasActiveTickets)
        {
            return Result.Failure(new Error("Session.CannotReschedule", 
                "Cannot reschedule session because tickets have already been sold."));
        }

        try
        {
            await schedulingService.RescheduleSessionAsync(session, request.NewStartTime, ct);
            await context.SaveChangesAsync(ct);
            
            return Result.Success();
        }
        catch (DbUpdateException ex)
        {
            if (ex.InnerException != null && ex.InnerException.Message.Contains("no_overlapping_sessions"))
            {
                return Result.Failure(new Error("Session.Overlap", 
                    "Rescheduling failed. New time slot overlaps with another session."));
            }
            return Result.Failure(new Error("Session.UpdateFailed", ex.Message));
        }
    }
}