using System.Data.Common;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Services;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.RescheduleSession;

public class RescheduleSessionCommandHandler(
    SessionSchedulingService schedulingService,
    IApplicationDbContext context) 
    : IRequestHandler<RescheduleSessionCommand, Result>
{
    public Task<Result> Handle(RescheduleSessionCommand request, CancellationToken ct)
        => SessionLifecycleTransaction.ExecuteCommandAsync(context, request.SessionId, async (session, token) =>
    {
        ct = token;
        var sessionId = new EntityId<Session>(request.SessionId);

        if (session == null)
        {
            return Result.Failure(new Error("Session.NotFound", "Session not found."));
        }
        
        var hasActiveTickets = await context.Tickets.AnyAsync(t =>
            t.SessionId == sessionId &&
            (t.TicketStatus == TicketStatus.Reserved || t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used), ct);

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
        catch (DomainException ex)
        {
            return Result.Failure(new Error("Session.CannotReschedule", ex.Message));
        }
        catch (DbUpdateException ex) when (ex.InnerException is not DbException { SqlState: "55P03" })
        {
            if (ex.InnerException != null && ex.InnerException.Message.Contains("no_overlapping_sessions"))
            {
                return Result.Failure(new Error("Session.Overlap", 
                    "Rescheduling failed. New time slot overlaps with another session."));
            }
            return Result.Failure(new Error("Session.UpdateFailed", ex.Message));
        }
    }, ct);
}
