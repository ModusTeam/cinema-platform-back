using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class MovieConfiguration : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasConversion(x => x.Value, x => new EntityId<Movie>(x));

        builder.Property(x => x.ExternalId).IsRequired();
        builder.HasIndex(x => x.ExternalId).IsUnique();

        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).IsRequired(false).HasMaxLength(2000);
        builder.Property(x => x.DurationMinutes).IsRequired();
        builder.Property(x => x.ReleaseYear).IsRequired();
        builder.Property(x => x.Rating).IsRequired().HasPrecision(4, 2);

        builder.Property(x => x.PosterUrl).IsRequired(false).HasMaxLength(500);
        builder.Property(x => x.BackdropUrl).IsRequired(false).HasMaxLength(500);
        builder.Property(x => x.TrailerUrl).IsRequired(false).HasMaxLength(500);
        
        builder.OwnsMany(m => m.Cast, a =>
        {
            a.ToJson();
        });
    }

}