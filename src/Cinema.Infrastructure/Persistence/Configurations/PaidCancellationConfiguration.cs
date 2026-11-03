using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public sealed class PaidCancellationConfiguration : IEntityTypeConfiguration<PaidCancellation>
{
    public void Configure(EntityTypeBuilder<PaidCancellation> b)
    {
        b.ToTable("paid_cancellations", t =>
        {
            t.HasCheckConstraint("paid_cancellation_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND order_id <> '00000000-0000-0000-0000-000000000000'::uuid AND user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND session_id <> '00000000-0000-0000-0000-000000000000'::uuid AND (actor_id IS NOT NULL OR admin_actor) AND (actor_id IS NULL OR actor_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
            t.HasCheckConstraint("paid_cancellation_amount", "paid_amount >= 0 AND points_used >= 0");
            t.HasCheckConstraint("paid_cancellation_currency", "paid_currency IS NULL OR paid_currency ~ '^[A-Z]{3}$'");
            t.HasCheckConstraint("paid_cancellation_snapshot", "jsonb_typeof(source_snapshot::jsonb) = 'object'");
            t.HasCheckConstraint("paid_cancellation_lease", "(execution_token IS NULL) = (lease_expires_at_utc IS NULL)");
            t.HasCheckConstraint("paid_cancellation_money", "money_state IN ('NotRequired','Ready','Dispatching','Succeeded','Unknown') AND (money_state = 'NotRequired') = (paid_amount = 0) AND (money_state = 'Succeeded') = (refund_reference IS NOT NULL AND length(btrim(refund_reference)) > 0)");
            t.HasCheckConstraint("paid_cancellation_points", "points_state IN ('NotRequired','Ready','Dispatching','Succeeded') AND (points_state = 'NotRequired') = (points_used = 0)");
            t.HasCheckConstraint("paid_cancellation_gold", "gold_state IN ('NotRequired','Ready','Dispatching','Succeeded') AND (gold_state = 'NotRequired') = (gold_ticket_id IS NULL)");
            t.HasCheckConstraint("paid_cancellation_finalized", "finalized_at_utc IS NULL OR (money_state IN ('NotRequired','Succeeded') AND points_state IN ('NotRequired','Succeeded') AND gold_state IN ('NotRequired','Succeeded'))");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.OrderId).HasConversion(x => x.Value, x => new EntityId<Order>(x));
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OrderId).IsUnique(); b.Ignore(x => x.CompensationComplete);
        b.Property(x => x.PaidAmount).HasPrecision(18, 2); b.Property(x => x.PaidCurrency).HasMaxLength(3);
        b.Property(x => x.SourceSnapshot).HasColumnType("text"); b.Property(x => x.RefundReference).HasColumnType("text");
        b.Property(x => x.ChargeReference).HasColumnType("text"); b.Property(x => x.LastCode).HasMaxLength(64);
        b.Property(x => x.MoneyState).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.PointsState).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.GoldState).HasConversion<string>().HasMaxLength(32);
    }
}
