using Cinema.Application.Services;
using Cinema.Domain.Exceptions;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.CancelSession;

public class CancelSessionCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CancelSessionCommand, Result>
{
    public Task<Result> Handle(CancelSessionCommand request, CancellationToken ct)
        => SessionLifecycleTransaction.ExecuteAsync(context, request.SessionId, async (session, token) =>
    {
        ct = token;
        var sessionId = new EntityId<Session>(request.SessionId);

        if (session == null)
        {
            return Result.Failure(new Error("Session.NotFound", "Session not found"));
        }
        
        var hasSoldTickets = await context.Tickets.AnyAsync(t =>
            t.SessionId == sessionId &&
            (t.TicketStatus == TicketStatus.Reserved || t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used), ct);

        if (hasSoldTickets)
        {
            return Result.Failure(new Error("Session.CannotCancel", 
                "Cannot cancel session with sold tickets. Please perform a refund procedure first."));
        }

        try
        {
            session.Cancel(DateTime.UtcNow);
            
            await context.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DomainException ex)
        {
            return Result.Failure(new Error("Session.CancelFailed", ex.Message));
        }
    }, ct);
}
