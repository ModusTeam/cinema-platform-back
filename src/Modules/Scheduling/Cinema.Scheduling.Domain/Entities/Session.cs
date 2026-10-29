using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;

namespace Cinema.Domain.Entities;

public class Session
{
    public EntityId<Session> Id { get; }

    public DateTime StartTime { get; private set; }
    // Unknown for sessions persisted before screening/occupancy were separated.
    public DateTime? EndTime { get; private set; }
    public DateTime OccupancyEndTime { get; private set; }
    public SessionStatus Status { get; private set; }

    public EntityId<Movie> MovieId { get; private set; }
    public Movie? Movie { get; private set; }

    public EntityId<Hall> HallId { get; private set; }
    public Hall? Hall { get; private set; }

    public EntityId<Pricing> PricingId { get; private set; }
    public Pricing? Pricing { get; private set; }

    public bool IsLoyaltyPaymentAllowed { get; private set; } = true;

    public string EventType { get; private set; } = "STANDARD";

    private Session(
        EntityId<Session> id,
        DateTime startTime,
        DateTime? endTime,
        DateTime occupancyEndTime,
        SessionStatus status,
        EntityId<Movie> movieId,
        EntityId<Hall> hallId,
        EntityId<Pricing> pricingId,
        bool isLoyaltyPaymentAllowed,
        string eventType = "STANDARD")
    {
        Id = id;
        StartTime = startTime;
        EndTime = endTime;
        OccupancyEndTime = occupancyEndTime;
        Status = status;
        MovieId = movieId;
        HallId = hallId;
        PricingId = pricingId;
        IsLoyaltyPaymentAllowed = isLoyaltyPaymentAllowed;
        EventType = eventType;
    }
    
    public static Session Create(
        EntityId<Session> id,
        DateTime startTime,
        DateTime endTime,
        EntityId<Movie> movieId,
        EntityId<Hall> hallId,
        EntityId<Pricing> pricingId,
        bool isLoyaltyPaymentAllowed = true,
        string eventType = "STANDARD",
        DateTime? occupancyEndTime = null)
    {
        if (startTime < DateTime.UtcNow)
        {
            throw new DomainException("Cannot create a session in the past.");
        }

        if (endTime <= startTime)
        {
            throw new DomainException("Session end time must be after start time.");
        }

        DateTime occupiedUntil = occupancyEndTime ?? endTime;
        if (occupiedUntil < endTime)
            throw new DomainException("Hall occupancy cannot end before the screening.");

        return new Session(
            id,
            startTime,
            endTime,
            occupiedUntil,
            SessionStatus.Scheduled,
            movieId,
            hallId,
            pricingId,
            isLoyaltyPaymentAllowed,
            eventType
        );
    }

    public void SetLoyaltyPaymentAllowed(bool isAllowed)
    {
        IsLoyaltyPaymentAllowed = isAllowed;
    }

    public void Cancel(DateTime cancellationTime)
    {
        if (Status == SessionStatus.Cancelled) return;
        
        if (Status == SessionStatus.Ended)
        {
            throw new DomainException("Cannot cancel a session that has already ended.");
        }
        if (StartTime < cancellationTime && OccupancyEndTime > cancellationTime)
        {
             throw new DomainException("Cannot cancel a session that is currently running.");
        }

        Status = SessionStatus.Cancelled;
    }

    public void Reschedule(DateTime newStartTime, DateTime? newEndTime, DateTime? newOccupancyEndTime = null)
    {
        if (Status == SessionStatus.Cancelled)
        {
            throw new DomainException("Cannot reschedule a cancelled session.");
        }

        if (Status == SessionStatus.Ended || StartTime <= DateTime.UtcNow)
            throw new DomainException("Cannot reschedule a session that has already started.");

        if (newStartTime < DateTime.UtcNow)
        {
            throw new DomainException("Cannot reschedule to the past.");
        }
        
        DateTime occupiedUntil = newOccupancyEndTime ?? newEndTime
            ?? throw new DomainException("Hall occupancy end is required.");
        if (occupiedUntil <= newStartTime || newEndTime <= newStartTime || occupiedUntil < newEndTime ||
            (EndTime.HasValue && !newEndTime.HasValue))
        {
             throw new DomainException("New end time must be greater than start time.");
        }

        StartTime = newStartTime;
        EndTime = newEndTime;
        OccupancyEndTime = occupiedUntil;
        if (Status == SessionStatus.SoldOut)
        {
            Status = SessionStatus.OpenForSales;
        }
    }
}
