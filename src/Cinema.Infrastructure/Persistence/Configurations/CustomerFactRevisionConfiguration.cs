using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public sealed class CustomerFactRevisionConfiguration : IEntityTypeConfiguration<CustomerFactRevision>
{
    public void Configure(EntityTypeBuilder<CustomerFactRevision> builder)
    {
        builder.ToTable("customer_fact_revisions", table =>
        {
            table.HasCheckConstraint("customer_fact_revision_positive", "revision > 0");
            table.HasCheckConstraint("customer_fact_snapshot_identity", "(snapshot::jsonb->'data'->>'eventId') IS NOT NULL AND (snapshot::jsonb->'data'->>'userId') IS NOT NULL AND (snapshot::jsonb->'data'->>'revision') IS NOT NULL AND (snapshot::jsonb->'data'->>'eventId')::uuid = event_id AND (snapshot::jsonb->'data'->>'userId')::uuid = user_id AND (snapshot::jsonb->'data'->>'revision')::integer = revision");
        });
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).ValueGeneratedNever();
        builder.HasIndex(x => new { x.UserId, x.Revision }).IsUnique();
        builder.Property(x => x.Snapshot).HasColumnType("text").IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
