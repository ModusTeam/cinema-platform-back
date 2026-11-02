namespace Cinema.Application.Common.Interfaces;

public enum RefundOutcomeKind { Succeeded, Declined, Pending, Unknown }
public sealed record RefundProviderRequest(Guid OperationId, string IdempotencyKey, string ChargeReference, decimal Amount, string Currency);
public sealed record RefundProviderOutcome(RefundOutcomeKind Kind, string? ProviderReference = null,
    string? EvidenceCode = null, DateTime? ProviderOccurredAtUtc = null);
public sealed record RefundLookup(bool AuthoritativeNotFound, RefundProviderOutcome? Outcome);

// Capabilities are adapter guarantees, including idempotency retention and payload conflict rejection.
// A transport failure is never a definitive decline. No adapter is registered by this foundation.
public interface IRefundProvider
{
    bool SupportsStableIdempotency { get; }
    bool SupportsAuthoritativeLookup { get; }
    Task<RefundProviderOutcome> SubmitAsync(RefundProviderRequest request, CancellationToken ct);
    Task<RefundLookup> LookupAsync(RefundProviderRequest request, CancellationToken ct);
}
