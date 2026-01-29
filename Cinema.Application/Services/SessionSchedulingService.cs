using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Cinema.Application.Services;

public class SessionSchedulingService
{
    private readonly IApplicationDbContext _context;
    private readonly IMovieInfoProvider _movieProvider;

    public SessionSchedulingService(
        IApplicationDbContext context, 
        IMovieInfoProvider movieProvider)
    {
        _context = context;
        _movieProvider = movieProvider;
    }

    public async Task<Session> ScheduleSessionAsync(
        EntityId<Hall> hallId,
        EntityId<Movie> movieId,
        EntityId<Pricing> pricingId,
        DateTime startTime,
        int cleaningTimeMinutes,
        CancellationToken ct)
    {
        var durationMinutes = await _movieProvider.GetDurationMinutesAsync(movieId, ct);
        if (durationMinutes == null)
            throw new DomainException("Movie not found.");
        
        var sessionEndTime = startTime.AddMinutes(durationMinutes.Value);
        var occupyEndTime = sessionEndTime.AddMinutes(cleaningTimeMinutes);
        
        return Session.Create(
            EntityId<Session>.New(),
            startTime,
            occupyEndTime,
            movieId,
            hallId,
            pricingId
        );
    }

    public async Task RescheduleSessionAsync(Session session, DateTime newStartTime, CancellationToken ct)
    {
        var currentDuration = session.EndTime - session.StartTime;
        var newEndTime = newStartTime.Add(currentDuration);

        var hasOverlap = await _context.Sessions
            .AnyAsync(s => 
                s.Id != session.Id &&
                s.HallId == session.HallId &&
                s.Status != SessionStatus.Cancelled &&
                s.StartTime < newEndTime && 
                s.EndTime > newStartTime, 
                ct);

        if (hasOverlap)
            throw new DomainException("Rescheduling failed. Overlap detected.");

        session.Reschedule(newStartTime, newEndTime);
    }
}