using Cinema.Domain.Common;
namespace Cinema.Domain.Entities;
// Source evidence retained independently of transport retention.
public sealed class SessionCompletionFact
{
    private SessionCompletionFact() { }
    public Guid EventId { get; private set; }
    public EntityId<Session> SessionId { get; private set; }
    public int Revision { get; private set; }
    public DateTime CompletedAtUtc { get; private set; }
    public string Snapshot { get; private set; } = string.Empty;
    public static SessionCompletionFact Create(Guid eventId, EntityId<Session> sessionId, DateTime end, string snapshot)
        => new() { EventId = eventId, SessionId = sessionId, Revision = 1, CompletedAtUtc = end, Snapshot = snapshot };
}
