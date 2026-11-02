using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Cinema.Infrastructure.Persistence.Configurations;

public sealed class RefundOperationConfiguration : IEntityTypeConfiguration<RefundOperation>
{
    public void Configure(EntityTypeBuilder<RefundOperation> b)
    {
        b.ToTable("refund_operations", t =>
        {
            t.HasCheckConstraint("refund_amount_positive", "amount > 0");
            t.HasCheckConstraint("refund_key_identity", "provider_key = 'refund:' || replace(id::text, '-', '')");
            t.HasCheckConstraint("refund_approval_time", "approved_at_utc >= paid_at_utc");
            t.HasCheckConstraint("refund_admission_object", "jsonb_typeof(admission_evidence::jsonb) = 'object'");
            t.HasCheckConstraint("refund_currency", "currency ~ '^[A-Z]{3}$'");
            t.HasCheckConstraint("refund_attempts", "attempts >= 0 AND attempts <= 8");
            t.HasCheckConstraint("refund_claim_pair", "(lease_token IS NULL) = (lease_expires_at_utc IS NULL)");
            t.HasCheckConstraint("refund_state", "state IN ('Approved','Dispatching','Pending','Unknown','Succeeded','Declined','ReconciliationRequired')");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.OrderId).HasConversion(x => x.Value, x => new EntityId<Order>(x));
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OrderId).IsUnique(); b.HasIndex(x => x.ProviderKey).IsUnique();
        b.Property(x => x.ProviderKey).HasMaxLength(64); b.Property(x => x.ChargeReference).HasMaxLength(256);
        b.Property(x => x.Amount).HasPrecision(18, 2); b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Reason).HasMaxLength(1000); b.Property(x => x.AdmissionEvidence).HasColumnType("text");
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(32); b.Property(x => x.LastCode).HasMaxLength(64);
        b.HasIndex(x => new { x.State, x.NextAttemptAtUtc });
    }
}
public sealed class RefundProviderObservationConfiguration : IEntityTypeConfiguration<RefundProviderObservation>
{
    public void Configure(EntityTypeBuilder<RefundProviderObservation> b)
    {
        b.ToTable("refund_provider_observations", t => t.HasCheckConstraint("refund_observation_outcome", "outcome IN ('Pending','Unknown','Succeeded','Declined','ReconciliationRequired')"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.HasOne<RefundOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OperationId, x.ClaimToken }).IsUnique();
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.ProviderReference).HasMaxLength(256); b.Property(x => x.EvidenceCode).HasMaxLength(64);
    }
}
