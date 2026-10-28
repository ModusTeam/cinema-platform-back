using Microsoft.AspNetCore.Identity;

namespace Cinema.Domain.Entities;

public class User : IdentityUser<Guid>
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public DateTime? RegisteredAtUtc { get; private set; }
    public DateTime? DobFirstSetAtUtc { get; private set; }
    public int CustomerFactsRevision { get; private set; }

    // The accepted operation's instant becomes authoritative only on commit.
    public void RecordRegistration(DateTime occurredAtUtc)
    {
        if (CustomerFactsRevision != 0 || RegisteredAtUtc.HasValue || DateOfBirth.HasValue || DobFirstSetAtUtc.HasValue)
            throw new InvalidOperationException("Registration facts already initialized");
        RegisteredAtUtc = MillisecondInstant(occurredAtUtc);
        CustomerFactsRevision = 1;
    }

    public void RecordFirstDateOfBirth(DateOnly date, DateTime occurredAtUtc)
    {
        if (DateOfBirth.HasValue || DobFirstSetAtUtc.HasValue)
            throw new InvalidOperationException("Date of birth already set");
        int nextRevision = checked(CustomerFactsRevision + 1);
        DateTime instant = MillisecondInstant(occurredAtUtc);
        DateOfBirth = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DobFirstSetAtUtc = instant;
        CustomerFactsRevision = nextRevision;
    }

    private static DateTime MillisecondInstant(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC source instant required", nameof(value));
        return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
