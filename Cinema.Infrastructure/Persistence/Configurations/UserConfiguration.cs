using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
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