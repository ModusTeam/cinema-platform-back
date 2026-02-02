using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Persistence;

public class ApplicationDbContextInitializer
{
    private readonly ILogger<ApplicationDbContextInitializer> _logger;
    private readonly ApplicationDbContext _context;
    private readonly RoleManager<IdentityRole<Guid>> _roleManager;

    public ApplicationDbContextInitializer(
        ILogger<ApplicationDbContextInitializer> logger, 
        ApplicationDbContext context,
        UserManager<User> userManager,
        RoleManager<IdentityRole<Guid>> roleManager)
    {
        _logger = logger;
        _context = context;
        _roleManager = roleManager;
    }

    public async Task InitialiseAsync()
    {
        try
        {
            if (_context.Database.IsNpgsql())
            {
                await _context.Database.MigrateAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initialising the database.");
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
            _logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }

    public async Task TrySeedAsync()
    {
        // 1. ROles
        var roles = new[] { "Admin", "User" };
        foreach (var role in roles)
        {
            if (!await _roleManager.RoleExistsAsync(role))
            {
                await _roleManager.CreateAsync(new IdentityRole<Guid>(role));
            }
        }

        // (Seat Types)
        if (!await _context.SeatTypes.AnyAsync())
        {
            _logger.LogInformation("Seeding Seat Types...");
            
            var standard = SeatType.New(EntityId<SeatType>.New(), "Standard", "Звичайне місце");
            var vip = SeatType.New(EntityId<SeatType>.New(), "VIP", "Місце підвищеного комфорту");
            var sofa = SeatType.New(EntityId<SeatType>.New(), "Sofa", "Диван для двох");

            await _context.SeatTypes.AddRangeAsync(standard, vip, sofa);
            await _context.SaveChangesAsync();
        }
        
        if (!await _context.Halls.AnyAsync())
        {
            _logger.LogInformation("Seeding Halls...");

            var standardType = await _context.SeatTypes.FirstAsync(st => st.Name == "Standard");
            var vipType = await _context.SeatTypes.FirstAsync(st => st.Name == "VIP");
            
            var grandHall = Hall.Create(EntityId<Hall>.New(), "IMAX Hall 1");

            grandHall.GenerateSeatsGrid(8, 12, standardType.Id);

            foreach (var seat in grandHall.Seats.Where(s => int.Parse(s.RowLabel) >= 7))
            {
                seat.ChangeType(vipType.Id);
            }

            var cozyHall = Hall.Create(EntityId<Hall>.New(), "Cozy Hall 2");
            cozyHall.GenerateSeatsGrid(5, 8, standardType.Id);

            await _context.Halls.AddRangeAsync(grandHall, cozyHall);
            await _context.SaveChangesAsync();
        }
        
        if (!await _context.Pricings.AnyAsync())
        {
            _logger.LogInformation("Seeding Pricings...");
            
            var pricing = Pricing.New(EntityId<Pricing>.New(), "Standard Evening");
            await _context.Pricings.AddAsync(pricing);
            
            var seatTypes = await _context.SeatTypes.ToListAsync();
            var standardType = seatTypes.First(st => st.Name == "Standard");
            var vipType = seatTypes.First(st => st.Name == "VIP");
            var sofaType = seatTypes.First(st => st.Name == "Sofa");
            
            var items = new List<PricingItem>
            {
                PricingItem.New(EntityId<PricingItem>.New(), 150.0m, pricing.Id, standardType.Id, DayOfWeek.Friday, null, null),
                PricingItem.New(EntityId<PricingItem>.New(), 150.0m, pricing.Id, standardType.Id, DayOfWeek.Saturday, null, null),
                PricingItem.New(EntityId<PricingItem>.New(), 150.0m, pricing.Id, standardType.Id, DayOfWeek.Sunday, null, null),
                
                PricingItem.New(EntityId<PricingItem>.New(), 120.0m, pricing.Id, standardType.Id, DayOfWeek.Monday, null, null),

                PricingItem.New(EntityId<PricingItem>.New(), 250.0m, pricing.Id, vipType.Id, DayOfWeek.Friday, null, null),
                PricingItem.New(EntityId<PricingItem>.New(), 250.0m, pricing.Id, vipType.Id, DayOfWeek.Saturday, null, null),
                
                PricingItem.New(EntityId<PricingItem>.New(), 400.0m, pricing.Id, sofaType.Id, DayOfWeek.Saturday, null, null)
            };

            await _context.PricingItems.AddRangeAsync(items);
            await _context.SaveChangesAsync();
        }
        
        if (!await _context.Technologies.AnyAsync())
        {
            _logger.LogInformation("Seeding Technologies...");
            var technologies = new[]
            {
                Technology.New(EntityId<Technology>.New(), "IMAX", "Visual"),
                Technology.New(EntityId<Technology>.New(), "Dolby Atmos", "Audio"),
                Technology.New(EntityId<Technology>.New(), "3D", "Visual"),
                Technology.New(EntityId<Technology>.New(), "4DX", "Experience"),
                Technology.New(EntityId<Technology>.New(), "D-Box", "Seating")
            };

            await _context.Technologies.AddRangeAsync(technologies);
            await _context.SaveChangesAsync();
        }

        // (Sessions)
        if (!await _context.Sessions.AnyAsync())
        {
            _logger.LogInformation("Seeding Sessions...");

            var hall = await _context.Halls.FirstAsync(h => h.Name == "IMAX Hall 1");
            var movie = await _context.Movies.FirstAsync(m => m.Title == "Dune: Part Two");
            var pricing = await _context.Pricings.FirstAsync();

            var sessions = new List<Session>();
            var today = DateTime.UtcNow.Date;

            for (int day = 0; day < 3; day++)
            {
                var currentDate = today.AddDays(day);

                var startTimes = new[] { 10, 14, 19 };

                foreach (var hour in startTimes)
                {
                    var start = currentDate.AddHours(hour);
                    var end = start.AddMinutes(movie.DurationMinutes);

                    var session = Session.Create(
                        EntityId<Session>.New(),
                        start,
                        end,
                        movie.Id,
                        hall.Id,
                        pricing.Id
                    );
                    sessions.Add(session);
                }
            }

            await _context.Sessions.AddRangeAsync(sessions);
            await _context.SaveChangesAsync();
        }
    }
}