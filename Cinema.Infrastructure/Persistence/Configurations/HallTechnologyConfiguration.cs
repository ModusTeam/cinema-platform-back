using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class HallTechnologyConfiguration : IEntityTypeConfiguration<HallTechnology>
{
    public void Configure(EntityTypeBuilder<HallTechnology> builder)
    {
        builder.HasKey(x => new { x.HallId, x.TechnologyId });

        builder.Property(x => x.HallId)
            .HasConversion(x => x.Value, x => new EntityId<Hall>(x));

        builder.HasOne(x => x.Hall)
            .WithMany(x => x.Technologies)
            .HasForeignKey(x => x.HallId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.TechnologyId)
            .HasConversion(x => x.Value, x => new EntityId<Technology>(x));

        builder.HasOne(x => x.Technology)
            .WithMany()
            .HasForeignKey(x => x.TechnologyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}