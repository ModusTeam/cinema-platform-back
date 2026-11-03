using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public enum CancellationStepState { NotRequired, Ready, Dispatching, Succeeded, Unknown }

// One retained lifecycle owner, including zero-cash compensation. This is not a
// second monetary refund instruction and cannot coexist with RefundOperation.
public sealed class PaidCancellation
{
    private PaidCancellation() { }
    public Guid Id { get; private set; }
    public EntityId<Order> OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? ActorId { get; private set; }
    public bool AdminActor { get; private set; }
    public Guid SessionId { get; private set; }
    public string? ChargeReference { get; private set; }
    public decimal PaidAmount { get; private set; }
    public string? PaidCurrency { get; private set; }
    public DateTime? PaidAtUtc { get; private set; }
    public int PointsUsed { get; private set; }
    public Guid? GoldTicketId { get; private set; }
    public string SourceSnapshot { get; private set; } = null!;
    public DateTime ClaimedAtUtc { get; private set; }
    public CancellationStepState MoneyState { get; private set; }
    public CancellationStepState PointsState { get; private set; }
    public CancellationStepState GoldState { get; private set; }
    public string? RefundReference { get; private set; }
    public Guid? ExecutionToken { get; private set; }
    public DateTime? LeaseExpiresAtUtc { get; private set; }
    public DateTime? FinalizedAtUtc { get; private set; }
    public string? LastCode { get; private set; }

    public static PaidCancellation Claim(Guid id, Order order, Guid? actor, bool admin,
        Guid? goldTicketId, string snapshot, DateTime now)
    {
        if (id == Guid.Empty || order.Id.Value == Guid.Empty || order.UserId == Guid.Empty || order.SessionId.Value == Guid.Empty ||
            actor == Guid.Empty || actor is null && !admin)
            throw new ArgumentException("Cancellation requires a stable operation and authenticated source identity");
        return new()
        {
            Id = id, OrderId = order.Id, UserId = order.UserId, ActorId = actor, AdminActor = admin,
            SessionId = order.SessionId.Value, ChargeReference = order.PaymentTransactionId,
            PaidAmount = order.PaidAmount, PaidCurrency = order.PaidCurrency, PaidAtUtc = order.PaidAt,
            PointsUsed = order.PointsUsed, GoldTicketId = goldTicketId, SourceSnapshot = snapshot, ClaimedAtUtc = now,
            MoneyState = order.PaidAmount == 0 ? CancellationStepState.NotRequired : CancellationStepState.Ready,
            PointsState = order.PointsUsed == 0 ? CancellationStepState.NotRequired : CancellationStepState.Ready,
            GoldState = goldTicketId is null ? CancellationStepState.NotRequired : CancellationStepState.Ready
        };
    }

    public bool TryAcquire(Guid token, DateTime now)
    {
        if (FinalizedAtUtc is not null || LeaseExpiresAtUtc > now) return false;
        // Lease expiry says nothing about the provider's external effect.
        if (MoneyState == CancellationStepState.Dispatching) MoneyState = CancellationStepState.Unknown;
        ExecutionToken = token; LeaseExpiresAtUtc = now.AddMinutes(2); return true;
    }

    public bool Owns(Guid token) => ExecutionToken == token;
    public void BeginMoney(Guid token)
    {
        if (!Owns(token) || MoneyState != CancellationStepState.Ready) throw new InvalidOperationException("Money dispatch is not owned");
        MoneyState = CancellationStepState.Dispatching;
    }
    public void ObserveMoney(string? reference)
    {
        if (MoneyState is not (CancellationStepState.Dispatching or CancellationStepState.Unknown))
            throw new InvalidOperationException("No monetary attempt to observe");
        if (string.IsNullOrWhiteSpace(reference)) { MoneyState = CancellationStepState.Unknown; LastCode = "Payment.RefundReconciliationRequired"; }
        else { MoneyState = CancellationStepState.Succeeded; RefundReference = reference; LastCode = null; }
    }
    public void CompletePoints(Guid token) { RequireOwner(token); PointsState = CancellationStepState.Succeeded; }
    public void CompleteGold(Guid token) { RequireOwner(token); GoldState = CancellationStepState.Succeeded; }
    public void BeginPoints(Guid token) { RequireOwner(token); if (PointsState == CancellationStepState.Ready) PointsState = CancellationStepState.Dispatching; }
    public void BeginGold(Guid token) { RequireOwner(token); if (GoldState == CancellationStepState.Ready) GoldState = CancellationStepState.Dispatching; }
    public void Release(Guid token, string? code)
    {
        if (!Owns(token)) return;
        ExecutionToken = null; LeaseExpiresAtUtc = null; LastCode = code;
    }
    public bool CompensationComplete => MoneyState is CancellationStepState.NotRequired or CancellationStepState.Succeeded &&
        PointsState is CancellationStepState.NotRequired or CancellationStepState.Succeeded &&
        GoldState is CancellationStepState.NotRequired or CancellationStepState.Succeeded;
    public void FinalizeCancellation(Guid token, DateTime now)
    {
        RequireOwner(token);
        if (!CompensationComplete) throw new InvalidOperationException("Compensation is incomplete");
        FinalizedAtUtc = now; Release(token, null);
    }
    private void RequireOwner(Guid token)
    { if (!Owns(token)) throw new InvalidOperationException("Cancellation execution is not owned"); }
}
