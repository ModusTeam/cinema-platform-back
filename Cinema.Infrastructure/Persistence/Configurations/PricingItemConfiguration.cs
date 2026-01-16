using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class PricingItemConfiguration : IEntityTypeConfiguration<PricingItem>
{
    public void Configure(EntityTypeBuilder<PricingItem> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(x => x.Value, x => new EntityId<PricingItem>(x));

        builder.Property(x => x.Price)
            .IsRequired()
            .HasPrecision(18, 2);

        builder.Property(x => x.DayOfWeek)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(x => x.StartTime)
            .HasConversion(new DateTimeUtcConverter())
            .IsRequired(false);

        builder.Property(x => x.EndTime)
            .HasConversion(new DateTimeUtcConverter())
            .IsRequired(false);

        builder.Property(x => x.PricingId)
            .HasConversion(x => x.Value, x => new EntityId<Pricing>(x));
        builder.HasOne(x => x.Pricing)
            .WithMany(x => x.PricingItems)
            .HasForeignKey(x => x.PricingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.SeatTypeId)
            .HasConversion(x => x.Value, x => new EntityId<SeatType>(x));
        builder.HasOne(x => x.SeatType)
            .WithMany()
            .HasForeignKey(x => x.SeatTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}