using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;

namespace Cinema.Domain.Entities;

public class Session
{
    public EntityId<Session> Id { get; }

    public DateTime StartTime { get; private set; }
    public DateTime EndTime { get; private set; }
    public SessionStatus Status { get; private set; }

    public EntityId<Movie> MovieId { get; private set; }
    public Movie? Movie { get; private set; }

    public EntityId<Hall> HallId { get; private set; }
    public Hall? Hall { get; private set; }

    public EntityId<Pricing> PricingId { get; private set; }
    public Pricing? Pricing { get; private set; }

    public ICollection<Ticket> Tickets { get; private set; } = [];


    private Session(
        EntityId<Session> id,
        DateTime startTime,
        DateTime endTime,
        SessionStatus status,
        EntityId<Movie> movieId,
        EntityId<Hall> hallId,
        EntityId<Pricing> pricingId)
    {
        Id = id;
        StartTime = startTime;
        EndTime = endTime;
        Status = status;
        MovieId = movieId;
        HallId = hallId;
        PricingId = pricingId;
    }

    public static Session New(
        EntityId<Session> id,
        DateTime startTime,
        DateTime endTime,
        SessionStatus status,
        EntityId<Movie> movieId,
        EntityId<Hall> hallId,
        EntityId<Pricing> pricingId
    ) => new(id, startTime, endTime, status, movieId, hallId, pricingId);
    
    public void Cancel(DateTime cancellationTime)
    {
        if (Status == SessionStatus.Cancelled) return;
        if (StartTime < cancellationTime)
        {
            throw new DomainException("Cannot cancel a session that has already started.");
        }

        Status = SessionStatus.Cancelled;
    }
    
    public void Reschedule(DateTime newStartTime, DateTime newEndTime)
    {
        if (Status == SessionStatus.Cancelled)
        {
            throw new DomainException("Cannot reschedule a cancelled session.");
        }

        if (newStartTime < DateTime.UtcNow)
        {
            throw new DomainException("Cannot reschedule to the past.");
        }
        
        if (StartTime < DateTime.UtcNow)
        {
            throw new DomainException("Cannot reschedule a session that has already started.");
        }

        StartTime = newStartTime;
        EndTime = newEndTime;
    }
}