using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class PricingItemConfiguration : IEntityTypeConfiguration<PricingItem>
{
    public void Configure(EntityTypeBuilder<PricingItem> builder)
    {
        builder.Property(p => p.Price).HasPrecision(18, 2);
    }
}