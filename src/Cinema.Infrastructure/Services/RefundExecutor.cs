using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
namespace Cinema.Infrastructure.Services;

// Deliberately not registered with DI, an API, or a background scheduler in foundation PR.
public sealed class RefundExecutor(RefundOperationStore store, IRefundProvider provider)
{
    public async Task<RefundState?> ExecuteAsync(Guid id, CancellationToken ct)
    {
        RefundClaim? claim = await store.ClaimAsync(id, ct);
        if (claim is null) return (await store.GetAsync(id, ct))?.State;
        RefundProviderOutcome? outcome = await store.SuccessfulObservationAsync(id, ct);
        bool review = false;
        if (outcome is null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                // Await the actual task: cancellation cannot undo a provider effect.
                // An adapter must bound its network call; a late returned success is still observed.
                if (claim.FirstDispatch) outcome = await provider.SubmitAsync(claim.Request, timeout.Token);
                else
                {
                    RefundLookup lookup = provider.SupportsAuthoritativeLookup
                        ? await provider.LookupAsync(claim.Request, timeout.Token) : new(false, null);
                    outcome = lookup.Outcome;
                    if (outcome is null && provider.SupportsStableIdempotency)
                        outcome = await provider.SubmitAsync(claim.Request, timeout.Token);
                    else if (outcome is null) review = true;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            { outcome = new(RefundOutcomeKind.Unknown, EvidenceCode: "PROVIDER_CALL_UNKNOWN"); }
        }
        outcome ??= new(RefundOutcomeKind.Unknown, EvidenceCode: "AUTHORITATIVE_RECONCILIATION_REQUIRED");
        // Do not promote an incomplete success response to confirmed monetary evidence.
        if (outcome.Kind == RefundOutcomeKind.Succeeded && string.IsNullOrWhiteSpace(outcome.ProviderReference))
            outcome = new(RefundOutcomeKind.Unknown, EvidenceCode: "INVALID_PROVIDER_SUCCESS");
        // A disconnected caller must not erase a returned monetary outcome.
        using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.ObserveAsync(claim, outcome, persistence.Token);
        await store.ResolveAsync(claim, review ? RefundState.ReconciliationRequired : RefundOperationStore.State(outcome.Kind), outcome.EvidenceCode, persistence.Token);
        return (await store.GetAsync(id, persistence.Token))?.State;
    }
}
