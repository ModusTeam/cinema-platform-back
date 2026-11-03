using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
namespace Cinema.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    private static readonly HashSet<string> RefundMutableFields = [nameof(RefundOperation.State),nameof(RefundOperation.LeaseToken),
        nameof(RefundOperation.LeaseExpiresAtUtc),nameof(RefundOperation.DispatchStartedAtUtc),nameof(RefundOperation.NextAttemptAtUtc),
        nameof(RefundOperation.Attempts),nameof(RefundOperation.LastCode)];
    private static readonly HashSet<string> CancellationMutableFields = [nameof(PaidCancellation.MoneyState), nameof(PaidCancellation.PointsState),
        nameof(PaidCancellation.GoldState), nameof(PaidCancellation.RefundReference), nameof(PaidCancellation.ExecutionToken),
        nameof(PaidCancellation.LeaseExpiresAtUtc), nameof(PaidCancellation.FinalizedAtUtc), nameof(PaidCancellation.LastCode)];
    private void ValidateRefundWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var e in ChangeTracker.Entries<Order>())
            if (e.State == EntityState.Modified && e.Property(x => x.PaidCurrency).IsModified &&
                (e.Property(x => x.PaidAt).OriginalValue is not null || e.Property(x => x.PaymentTransactionId).OriginalValue is not null))
                throw new InvalidOperationException("Confirmed payment currency is immutable");
        foreach (var e in ChangeTracker.Entries<RefundOperation>())
        {
            if (e.State == EntityState.Deleted || e.State == EntityState.Modified && e.Properties.Any(p => p.IsModified && !RefundMutableFields.Contains(p.Metadata.Name)))
                throw new InvalidOperationException("Refund identity and approval evidence are immutable");
        }
        foreach (var e in ChangeTracker.Entries<RefundProviderObservation>())
            if (e.State is EntityState.Modified or EntityState.Deleted) throw new InvalidOperationException("Refund provider evidence is append-only");
        foreach (var e in ChangeTracker.Entries<PaidCancellation>())
        {
            if (e.State == EntityState.Deleted || e.State == EntityState.Modified &&
                e.Properties.Any(p => p.IsModified && !CancellationMutableFields.Contains(p.Metadata.Name)))
                throw new InvalidOperationException("Paid cancellation identity and source evidence are immutable");
            if (e.State != EntityState.Modified) continue;
            foreach (string field in new[] { nameof(PaidCancellation.MoneyState), nameof(PaidCancellation.PointsState), nameof(PaidCancellation.GoldState) })
                if (e.Property(field).OriginalValue is CancellationStepState.Succeeded && e.Property(field).CurrentValue is not CancellationStepState.Succeeded)
                    throw new InvalidOperationException("Successful cancellation compensation is retained");
            if (e.Property(x => x.RefundReference).OriginalValue is not null && e.Property(x => x.RefundReference).IsModified ||
                e.Property(x => x.FinalizedAtUtc).OriginalValue is not null && e.Property(x => x.FinalizedAtUtc).IsModified)
                throw new InvalidOperationException("Successful cancellation evidence is retained");
        }
    }
}
