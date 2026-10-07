using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class SeatLockConfiguration : IEntityTypeConfiguration<SeatLock>
{
    public void Configure(EntityTypeBuilder<SeatLock> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(x => x.Value, x => new EntityId<SeatLock>(x));

        builder.Property(x => x.ExpiryTime)
            .HasConversion(new DateTimeUtcConverter());

        builder.Property(x => x.SessionId)
            .HasConversion(x => x.Value, x => new EntityId<Session>(x));

        builder.HasOne(x => x.Session)
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade); // Можна видалити лок, якщо сеанс видалено

        builder.Property(x => x.SeatId)
            .HasConversion(x => x.Value, x => new EntityId<Seat>(x));

        builder.HasOne(x => x.Seat)
            .WithMany()
            .HasForeignKey(x => x.SeatId)
            .OnDelete(DeleteBehavior.Cascade);
        

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}