using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Persistence;

public class ApplicationDbContextInitializer(
    ILogger<ApplicationDbContextInitializer> logger,
    ApplicationDbContext dbContext)
{
    public async Task InitialiseAsync()
    {
        try
        {
            if (dbContext.Database.IsRelational())
            {
                await dbContext.Database.MigrateAsync();
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "An error occurred while initialising the database.");
            throw;
        }
    }
    
    public async Task SeedAsync()
    {
        try
        {
            await TrySeedAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }

    public async Task TrySeedAsync()
    {
        // 1. SEAT TYPES
        var standardTypeId = new EntityId<SeatType>(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var vipTypeId = new EntityId<SeatType>(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        if (!await dbContext.SeatTypes.AnyAsync())
        {
            await dbContext.SeatTypes.AddRangeAsync(
                SeatType.New(standardTypeId, "Standard", "Звичайне зручне крісло"),
                SeatType.New(vipTypeId, "VIP", "Шкіряне крісло-реклайнер")
            );
            await dbContext.SaveChangesAsync();
        }

        // 2. TECHNOLOGIES
        var imaxId = new EntityId<Technology>(Guid.Parse("88888888-8888-8888-8888-888888888888"));
        var dolbyId = new EntityId<Technology>(Guid.Parse("99999999-9999-9999-9999-999999999999"));

        if (!await dbContext.Technologies.AnyAsync())
        {
            await dbContext.Technologies.AddRangeAsync(
                Technology.New(imaxId, "IMAX", "Visual"),
                Technology.New(dolbyId, "Dolby Atmos", "Audio"),
                Technology.New(new EntityId<Technology>(Guid.NewGuid()), "3D", "Visual"),
                Technology.New(new EntityId<Technology>(Guid.NewGuid()), "4DX", "Experience")
            );
            await dbContext.SaveChangesAsync();
        }

        // 3. GENRES
        if (!await dbContext.Genres.AnyAsync())
        {
            await dbContext.Genres.AddRangeAsync(
                Genre.New(new EntityId<Genre>(Guid.NewGuid()), 28, "Action", "action"),
                Genre.New(new EntityId<Genre>(Guid.NewGuid()), 878, "Sci-Fi", "sci-fi"),
                Genre.New(new EntityId<Genre>(Guid.NewGuid()), 18, "Drama", "drama")
            );
            await dbContext.SaveChangesAsync();
        }

        // 4. MOVIES
        if (!await dbContext.Movies.AnyAsync())
        {
            var inception = Movie.New(
                new EntityId<Movie>(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                27205,
                "Inception",
                148,
                8.8m,
                "https://www.themoviedb.org/t/p/w600_and_h900_face/9gk7adHYeDvHkCSEqAvQNLV5Uge.jpg",
                "https://www.youtube.com/watch?v=YoHD9XEInc0"
            );

            var matrix = Movie.New(
                new EntityId<Movie>(Guid.NewGuid()),
                603,
                "The Matrix",
                136,
                8.7m,
                "https://www.themoviedb.org/t/p/w600_and_h900_face/ahxs2iYHjp6dMHjJORdF5K0deHm.jpg",
                "https://www.youtube.com/watch?v=vKQi3bBA1y8"
            );

            await dbContext.Movies.AddRangeAsync(inception, matrix);
            await dbContext.SaveChangesAsync();
        }

        // 5. PRICING
        if (!await dbContext.Pricings.AnyAsync())
        {
            var defaultPricingId = new EntityId<Pricing>(Guid.Parse("44444444-4444-4444-4444-444444444444"));
            var pricing = Pricing.New(defaultPricingId, "Base Tariff 2026");
            
            dbContext.Pricings.Add(pricing);
            await dbContext.SaveChangesAsync();

            var pricingItems = new List<PricingItem>();

            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                pricingItems.Add(PricingItem.New(
                    new EntityId<PricingItem>(Guid.NewGuid()),
                    120.00m,
                    defaultPricingId,
                    standardTypeId,
                    day,
                    null, null
                ));

                pricingItems.Add(PricingItem.New(
                    new EntityId<PricingItem>(Guid.NewGuid()),
                    250.00m,
                    defaultPricingId,
                    vipTypeId,
                    day,
                    null, null
                ));
            }
            
            dbContext.PricingItems.AddRange(pricingItems);
            await dbContext.SaveChangesAsync();
        }

        // 6. HALLS & SEATS
        if (!await dbContext.Halls.AnyAsync())
        {
            var hallId = new EntityId<Hall>(Guid.Parse("55555555-5555-5555-5555-555555555555"));
            var hall = Hall.Create(hallId, "IMAX Hall 1");
            hall.GenerateSeatsGrid(10, 15, standardTypeId);
            
            hall.UpdateTechnologies(new List<EntityId<Technology>> { imaxId, dolbyId });
            
            dbContext.Halls.Add(hall);

            await dbContext.SaveChangesAsync();
        }
    }
}