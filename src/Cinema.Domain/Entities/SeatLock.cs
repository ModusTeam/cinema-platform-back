using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class SeatLock
{
    public EntityId<SeatLock> Id { get; }
    public DateTime ExpiryTime { get; private set; }

    public EntityId<Session> SessionId { get; private set; }
    public Session? Session { get; private set; }

    public EntityId<Seat> SeatId { get; private set; }
    public Seat? Seat { get; private set; }

    public Guid UserId { get; private set; }
    public User? User { get; private set; }


    private SeatLock(
        EntityId<SeatLock> id,
        DateTime expiryTime,
        EntityId<Session> sessionId,
        EntityId<Seat> seatId,
        Guid userId)
    {
        Id = id;
        ExpiryTime = expiryTime;
        SessionId = sessionId;
        SeatId = seatId;
        UserId = userId;
    }

    public static SeatLock New(
        EntityId<SeatLock> id,
        DateTime expiryTime,
        EntityId<Session> sessionId,
        EntityId<Seat> seatId,
        Guid userId) => new(id, expiryTime, sessionId, seatId, userId);
}