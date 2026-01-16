using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
    public void Configure(EntityTypeBuilder<Seat> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(x => x.Value, x => new EntityId<Seat>(x));

        builder.Property(x => x.RowLabel)
            .IsRequired();
        
        builder.Property(x => x.Number)
            .IsRequired();
            
        builder.Property(x => x.Status)
            .IsRequired()
            .HasConversion<int>(); 

        builder.Property(x => x.HallId)
            .HasConversion(x => x.Value, x => new EntityId<Hall>(x));
        
        builder.HasOne(x => x.Hall)
            .WithMany(x => x.Seats)
            .HasForeignKey(x => x.HallId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.SeatTypeId)
            .HasConversion(x => x.Value, x => new EntityId<SeatType>(x));
        
        builder.HasOne(x => x.SeatType)
            .WithMany()
            .HasForeignKey(x => x.SeatTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}