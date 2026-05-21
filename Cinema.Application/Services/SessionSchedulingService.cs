using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Services;

public class SessionSchedulingService(
    IApplicationDbContext context, 
    IMovieInfoProvider movieProvider)
{
    public async Task<Session> ScheduleSessionAsync(
        EntityId<Hall> hallId,
        EntityId<Movie> movieId,
        EntityId<Pricing> pricingId,
        DateTime startTime,
        int cleaningTimeMinutes,
        CancellationToken ct,
        bool isLoyaltyPaymentAllowed = true)
    {
        var durationMinutes = await movieProvider.GetDurationMinutesAsync(movieId, ct);
        if (durationMinutes == null)
            throw new DomainException("Movie not found.");
        
        var sessionEndTime = startTime.AddMinutes(durationMinutes.Value);
        var occupyEndTime = sessionEndTime.AddMinutes(cleaningTimeMinutes);

        await ValidateSessionOverlapAsync(hallId, startTime, occupyEndTime, null, ct);
        
        return Session.Create(
            EntityId<Session>.New(),
            startTime,
            occupyEndTime,
            movieId,
            hallId,
            pricingId,
            isLoyaltyPaymentAllowed
        );
    }

    public async Task RescheduleSessionAsync(Session session, DateTime newStartTime, CancellationToken ct)
    {
        var duration = session.EndTime - session.StartTime;
        var newEndTime = newStartTime.Add(duration);
        
        await ValidateSessionOverlapAsync(session.HallId, newStartTime, newEndTime, session.Id, ct);

        session.Reschedule(newStartTime, newEndTime);
    }

    private async Task ValidateSessionOverlapAsync(
        EntityId<Hall> hallId, 
        DateTime start, 
        DateTime end, 
        EntityId<Session>? excludeSessionId, 
        CancellationToken ct)
    {
        var query = context.Sessions
            .AsNoTracking()
            .Where(s => 
                s.HallId == hallId &&
                s.Status != SessionStatus.Cancelled &&
                s.StartTime < end && 
                s.EndTime > start);

        if (excludeSessionId != null)
        {
            query = query.Where(s => s.Id != excludeSessionId);
        }

        var hasOverlap = await query.AnyAsync(ct);

        if (hasOverlap)
            throw new DomainException("Time slot overlaps with an existing session in this hall.");
    }
}