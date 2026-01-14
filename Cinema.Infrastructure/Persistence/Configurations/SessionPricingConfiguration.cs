using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class SessionPricingConfiguration : IEntityTypeConfiguration<SessionPricing>
{
    public void Configure(EntityTypeBuilder<SessionPricing> builder)
    {
        builder.HasKey(sp => new { sp.SessionId, sp.PricingId });
        builder.HasOne(sp => sp.Session)
            .WithMany(s => s.SessionPricings)
            .HasForeignKey(sp => sp.SessionId);
        
        builder.HasOne(sp => sp.Pricing)
            .WithMany(p => p.SessionPricings)
            .HasForeignKey(sp => sp.PricingId);
    }
}