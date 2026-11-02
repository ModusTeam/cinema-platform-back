using System.Text.Json;
using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public enum RefundState { Approved, Dispatching, Pending, Unknown, Succeeded, Declined, ReconciliationRequired }

// An already-authorized instruction. This foundation does not evaluate H-16 or expose approval APIs.
public sealed class RefundOperation
{
    public const string ConflictingProviderOutcomesCode = "CONFLICTING_PROVIDER_OUTCOMES";
    private RefundOperation() { }
    public Guid Id { get; private set; }
    public EntityId<Order> OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid SessionId { get; private set; }
    public string ChargeReference { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTime PaidAtUtc { get; private set; }
    public DateTime ApprovedAtUtc { get; private set; }
    public Guid ActorId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string AdmissionEvidence { get; private set; } = string.Empty;
    public string ProviderKey { get; private set; } = string.Empty;
    public RefundState State { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTime? LeaseExpiresAtUtc { get; private set; }
    public DateTime? DispatchStartedAtUtc { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public string? LastCode { get; private set; }

    public static RefundOperation Create(Guid id, Guid order, Guid user, Guid session, string charge,
        decimal amount, string currency, DateTime paidAt, DateTime approvedAt, Guid actor, string reason, string admissionEvidence)
    {
        if (new[] { id, order, user, session, actor }.Any(x => x == Guid.Empty) ||
            string.IsNullOrWhiteSpace(charge) || charge.Length > 256 || amount <= 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount ||
            currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper) ||
            paidAt.Kind != DateTimeKind.Utc || approvedAt.Kind != DateTimeKind.Utc || approvedAt < paidAt ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || admissionEvidence.Length > 16000)
            throw new ArgumentException("Invalid full-cash refund instruction");
        using JsonDocument evidence = JsonDocument.Parse(admissionEvidence);
        if (evidence.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Admission evidence must be an object");
        return new()
        {
            Id = id,
            OrderId = new(order),
            UserId = user,
            SessionId = session,
            ChargeReference = charge,
            Amount = amount,
            Currency = currency,
            PaidAtUtc = Microseconds(paidAt),
            ApprovedAtUtc = Microseconds(approvedAt),
            ActorId = actor,
            Reason = reason,
            AdmissionEvidence = admissionEvidence,
            ProviderKey = "refund:" + id.ToString("N"),
            State = RefundState.Approved,
            NextAttemptAtUtc = Microseconds(approvedAt)
        };
    }

    public bool TryClaim(Guid token, DateTime now, TimeSpan lease)
    {
        if (token == Guid.Empty || now.Kind != DateTimeKind.Utc || lease <= TimeSpan.Zero) throw new ArgumentException("Invalid claim");
        if (State is RefundState.Succeeded or RefundState.Declined or RefundState.ReconciliationRequired ||
            NextAttemptAtUtc > now || LeaseExpiresAtUtc > now) return false;
        if (Attempts >= 8) { State = RefundState.ReconciliationRequired; LastCode = "AUTOMATIC_BUDGET_EXHAUSTED"; return false; }
        State = RefundState.Dispatching; LeaseToken = token; LeaseExpiresAtUtc = Microseconds(now + lease);
        DispatchStartedAtUtc ??= Microseconds(now); Attempts++;
        return true;
    }

    // Called only after database ownership validation; provider evidence is stored separately.
    public void Resolve(RefundState state, DateTime now, string? code)
    {
        if (!Enum.IsDefined(state) || State != RefundState.Dispatching || state is RefundState.Approved or RefundState.Dispatching)
            throw new InvalidOperationException("Refund resolution requires a dispatch claim");
        if (now.Kind != DateTimeKind.Utc || code?.Length > 64) throw new ArgumentException("Invalid resolution");
        State = state; LastCode = code; LeaseToken = null; LeaseExpiresAtUtc = null;
        NextAttemptAtUtc = Microseconds(now.AddSeconds(Math.Min(300, 10 * Math.Pow(2, Attempts - 1))));
    }
    public void ConfirmObservedSuccess(DateTime now)
    {
        if (State == RefundState.Declined || LastCode == ConflictingProviderOutcomesCode || LeaseExpiresAtUtc > now) throw new InvalidOperationException("Active or contradictory provider claim");
        State = RefundState.Succeeded; LeaseToken = null; LeaseExpiresAtUtc = null; LastCode = null;
    }
    public void FlagConflictingProviderOutcomes(DateTime now)
    {
        if (State == RefundState.Approved || LeaseExpiresAtUtc > now) throw new InvalidOperationException("Active or undispatched provider claim");
        State = RefundState.ReconciliationRequired; LastCode = ConflictingProviderOutcomesCode; LeaseToken = null; LeaseExpiresAtUtc = null;
    }
    private static DateTime Microseconds(DateTime value) => value.AddTicks(-(value.Ticks % 10));
}

// Append-only observations retain late successes without letting stale workers change a newer claim.
public sealed class RefundProviderObservation
{
    private RefundProviderObservation() { }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ClaimToken { get; private set; }
    public RefundState Outcome { get; private set; }
    public string? ProviderReference { get; private set; }
    public string? EvidenceCode { get; private set; }
    public DateTime? ProviderOccurredAtUtc { get; private set; }
    public DateTime ObservedAtUtc { get; private set; }
    public static RefundProviderObservation Create(Guid operation, Guid token, RefundState outcome,
        string? reference, string? code, DateTime? occurredAt, DateTime observedAt)
    {
        if (operation == Guid.Empty || token == Guid.Empty || observedAt.Kind != DateTimeKind.Utc ||
            occurredAt is { Kind: not DateTimeKind.Utc } || reference?.Length > 256 || code?.Length > 64 ||
            outcome is RefundState.Approved or RefundState.Dispatching ||
            outcome == RefundState.Succeeded && string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("Invalid provider observation");
        return new()
        {
            Id = Guid.NewGuid(),
            OperationId = operation,
            ClaimToken = token,
            Outcome = outcome,
            ProviderReference = reference,
            EvidenceCode = code,
            ProviderOccurredAtUtc = occurredAt?.AddTicks(-(occurredAt.Value.Ticks % 10)),
            ObservedAtUtc = observedAt.AddTicks(-(observedAt.Ticks % 10))
        };
    }
}
