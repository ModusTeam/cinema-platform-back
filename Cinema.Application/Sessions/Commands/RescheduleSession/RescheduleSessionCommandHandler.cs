using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.RescheduleSession;

public class RescheduleSessionCommandHandler : IRequestHandler<RescheduleSessionCommand, Result>
{
    private readonly IApplicationDbContext _context;

    public RescheduleSessionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result> Handle(RescheduleSessionCommand request, CancellationToken cancellationToken)
    {
        var sessionId = new EntityId<Session>(request.SessionId);

        var session = await _context.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session == null)
        {
            return Result.Failure(new Error("Session.NotFound", "Session not found."));
        }

        var duration = session.EndTime - session.StartTime;
        var newEndTime = request.NewStartTime.Add(duration);

        var isOverlapping = await _context.Sessions
            .AnyAsync(s =>
                    s.Id != sessionId &&
                    s.HallId == session.HallId &&
                    s.Status != SessionStatus.Cancelled &&
                    s.StartTime < newEndTime &&
                    s.EndTime > request.NewStartTime,
                cancellationToken);

        if (isOverlapping)
        {
            return Result.Failure(new Error("Session.Overlap", "The new time slot is already booked."));
        }

        try
        {
            session.Reschedule(request.NewStartTime, newEndTime);

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            return Result.Failure(new Error("Session.RescheduleFailed", ex.Message));
        }
        catch (DbUpdateException)
        {
            return Result.Failure(new Error("Session.Overlap", "Concurrency conflict: Slot taken."));
        }

        return Result.Success();
    }
}