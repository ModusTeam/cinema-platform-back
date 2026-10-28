namespace Cinema.Domain.Entities;

// Retained source evidence, independent of the transport outbox retention period.
public sealed class CustomerFactRevision
{
    private CustomerFactRevision() { }
    public Guid EventId { get; private set; }
    public Guid UserId { get; private set; }
    public int Revision { get; private set; }
    public string Snapshot { get; private set; } = string.Empty;

    public static CustomerFactRevision Create(Guid eventId, Guid userId, int revision, string snapshot)
    {
        if (revision < 1 || string.IsNullOrWhiteSpace(snapshot)) throw new ArgumentException("Invalid source revision");
        return new CustomerFactRevision { EventId = eventId, UserId = userId, Revision = revision, Snapshot = snapshot };
    }
}
