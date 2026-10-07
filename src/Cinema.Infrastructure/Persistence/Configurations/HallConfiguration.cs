using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class HallConfiguration : IEntityTypeConfiguration<Hall>
{
    public void Configure(EntityTypeBuilder<Hall> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasConversion(x => x.Value, x => new EntityId<Hall>(x));
        
        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);
        
        builder.HasIndex(x => x.Name)
            .IsUnique();
        
        builder.Property(x => x.TotalCapacity)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasDefaultValue(true)
            .IsRequired();
        
        builder.HasQueryFilter(h => h.IsActive);
        
        builder.Metadata.FindNavigation(nameof(Hall.Seats))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Metadata.FindNavigation(nameof(Hall.Technologies))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        
        builder.HasMany(h => h.Seats)
            .WithOne(s => s.Hall)
            .HasForeignKey(s => s.HallId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.HasMany(h => h.Technologies)
            .WithOne(ht => ht.Hall)
            .HasForeignKey(ht => ht.HallId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.HasMany(h => h.Sessions)
            .WithOne(s => s.Hall)
            .HasForeignKey(s => s.HallId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}