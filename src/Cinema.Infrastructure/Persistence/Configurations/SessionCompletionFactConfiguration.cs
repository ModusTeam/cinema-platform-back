using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Cinema.Infrastructure.Persistence.Configurations;
public sealed class SessionCompletionFactConfiguration : IEntityTypeConfiguration<SessionCompletionFact>
{
    public void Configure(EntityTypeBuilder<SessionCompletionFact> builder)
    {
        builder.ToTable("session_completion_facts", table => table.HasCheckConstraint("session_completion_v1", "revision = 1"));
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).ValueGeneratedNever();
        builder.Property(x => x.SessionId).HasConversion(x => x.Value, x => new EntityId<Session>(x));
        builder.HasIndex(x => new { x.SessionId, x.Revision }).IsUnique();
        builder.Property(x => x.CompletedAtUtc).HasPrecision(6);
        builder.Property(x => x.Snapshot).HasColumnType("text").IsRequired();
        builder.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}
