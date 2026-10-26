using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(x => x.RegisteredAtUtc).HasColumnType("timestamp(3) with time zone");
        builder.Property(x => x.DobFirstSetAtUtc).HasColumnType("timestamp(3) with time zone");
        builder.Property(x => x.CustomerFactsRevision).HasDefaultValue(0).IsConcurrencyToken();
        builder.ToTable(table => table.HasCheckConstraint("customer_facts_head_valid",
            "customer_facts_revision >= 0 AND (registered_at_utc IS NULL OR customer_facts_revision > 0) AND (dob_first_set_at_utc IS NULL OR (date_of_birth IS NOT NULL AND customer_facts_revision > 0))"));
        builder.Property(x => x.FirstName)
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(x => x.LastName)
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(x => x.DateOfBirth)
            .IsRequired(false)
            .HasConversion(new DateTimeUtcConverter());
    }
}
