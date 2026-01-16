using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(x => x.Value, x => new EntityId<Session>(x));

        builder.Property(x => x.StartTime)
            .IsRequired()
            .HasConversion(new DateTimeUtcConverter());

        builder.Property(x => x.EndTime)
            .IsRequired()
            .HasConversion(new DateTimeUtcConverter());

        builder.Property(x => x.Status)
            .HasConversion<int>();
        
        builder.Property(x => x.HallId)
            .HasConversion(x => x.Value, x => new EntityId<Hall>(x));

        builder.HasOne(x => x.Hall)
            .WithMany(x => x.Sessions)
            .HasForeignKey(x => x.HallId)
            .OnDelete(DeleteBehavior.Restrict); // Краще Restrict, щоб не видалити зал з сеансами

        builder.Property(x => x.MovieId)
            .HasConversion(x => x.Value, x => new EntityId<Movie>(x));
        
        builder.HasOne(x => x.Movie)
            .WithMany(x => x.Sessions)
            .HasForeignKey(x => x.MovieId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.PricingId)
            .HasConversion(x => x.Value, x => new EntityId<Pricing>(x));

        builder.HasOne(x => x.Pricing)
            .WithMany()
            .HasForeignKey(x => x.PricingId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}