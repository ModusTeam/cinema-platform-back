using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Persistence;

public class ApplicationDbContextInitializer
{
    private readonly ILogger<ApplicationDbContextInitializer> _logger;
    private readonly ApplicationDbContext _context;
    private readonly RoleManager<IdentityRole<Guid>> _roleManager;
    private readonly UserManager<User> _userManager;

    public ApplicationDbContextInitializer(
        ILogger<ApplicationDbContextInitializer> logger, 
        ApplicationDbContext context,
        UserManager<User> userManager,
        RoleManager<IdentityRole<Guid>> roleManager)
    {
        _logger = logger;
        _context = context;
        _roleManager = roleManager;
        _userManager = userManager;
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
        // 1. Roles
        var roles = new[] { "Admin", "User" };
        foreach (var role in roles)
        {
            if (!await _roleManager.RoleExistsAsync(role))
            {
                await _roleManager.CreateAsync(new IdentityRole<Guid>(role));
            }
        }

        // 2. Створюємо Адміністратора (для тестування і логіну)
        var adminEmail = "admin@cinema.com";
        var adminUser = await _userManager.FindByEmailAsync(adminEmail);

        if (adminUser == null)
        {
            _logger.LogInformation("Seeding Default Admin User...");
            
            var admin = new User
            {
                UserName = adminEmail,
                Email = adminEmail,
                FirstName = "System",
                LastName = "Admin",
                EmailConfirmed = true 
            };

            var result = await _userManager.CreateAsync(admin, "Admin123!");

            if (result.Succeeded)
            {
                await _userManager.AddToRoleAsync(admin, "Admin");
            }
            else
            {
                _logger.LogError("Failed to seed admin user: {Errors}", 
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }

        // ====================================================================
        // 3. ДОДАЄМО СІДИНГ КІНОТЕАТРУ (Фільм, Зал, Місця, Ціни, Сеанс)
        // ====================================================================
        if (!await _context.Movies.AnyAsync())
        {
            _logger.LogInformation("Seeding Cinema Data for Testing...");

            // А. Створюємо фільм
            var movie = Movie.CreateManual(
                title: "Дюна: Частина друга", 
                description: "Фантастичний епік від Дені Вільнева", 
                durationMinutes: 166,
                releaseYear: 2024,
                status: MovieStatus.Active
            );
            _context.Movies.Add(movie);

            // Б. Створюємо зал
            var hallId = EntityId<Hall>.New();
            var hall = Hall.Create(hallId, "IMAX Зал 1");
            _context.Halls.Add(hall);

            // В. Створюємо типи місць
            var standardSeatTypeId = EntityId<SeatType>.New();
            var standardSeatType = SeatType.New(standardSeatTypeId, "Standard", "Звичайне місце");
            
            var vipSeatTypeId = EntityId<SeatType>.New();
            var vipSeatType = SeatType.New(vipSeatTypeId, "VIP", "Диванчик для двох");
            
            _context.SeatTypes.AddRange(standardSeatType, vipSeatType);

            // Г. Створюємо кілька місць у залі
            // (Використовуємо твій фабричний метод: Id, HallId, SeatTypeId, Row, Number, RowLabel, NumberLabel)
            var seat1 = Seat.New(EntityId<Seat>.New(), "1", 1, 1, 1, SeatStatus.Active, hallId, standardSeatTypeId);
            var seat2 = Seat.New(EntityId<Seat>.New(), "1", 2, 1, 2, SeatStatus.Active, hallId, standardSeatTypeId);
            var seat3 = Seat.New(EntityId<Seat>.New(), "2", 1, 2, 1, SeatStatus.Active, hallId, vipSeatTypeId);
            
            // Застосовуємо розсадку (Твій доменний метод сам додасть місця і перерахує місткість)
            hall.ApplyLayout([seat1, seat2, seat3]);

            // Д. Створюємо прайсинг (ціни)
            var pricingId = EntityId<Pricing>.New();
            var pricing = Pricing.New(pricingId, "Стандартна ціна (Вечір)");
            _context.Pricings.Add(pricing);

            // Е. Прив'язуємо ціни до типів місць (null для днів/годин означає "завжди діє")
            var pricingItemStandard = PricingItem.New(EntityId<PricingItem>.New(), 150m, pricingId, standardSeatTypeId, null, null, null);
            var pricingItemVip = PricingItem.New(EntityId<PricingItem>.New(), 300m, pricingId, vipSeatTypeId, null, null, null);
            _context.PricingItems.AddRange(pricingItemStandard, pricingItemVip);

            // Є. Створюємо сам сеанс (на завтрашній день)
            var sessionId = EntityId<Session>.New();
            var startTime = DateTime.UtcNow.AddDays(1).AddHours(18); // Завтра о 18:00
            var endTime = startTime.AddMinutes(166);
            
            var session = Session.Create(sessionId, startTime, endTime, movie.Id, hallId, pricingId);
            _context.Sessions.Add(session);

            // Зберігаємо всю "пачку" даних у БД
            await _context.SaveChangesAsync(CancellationToken.None);
            
            // Виводимо ідентифікатори в консоль для зручного тестування
            _logger.LogInformation("==========================================");
            _logger.LogInformation("CINEMA DATA SEEDED SUCCESSFULLY!");
            _logger.LogInformation("Session ID: {SessionId}", session.Id.Value);
            _logger.LogInformation("Seat 1 (Standard 150 UAH): {SeatId}", seat1.Id.Value);
            _logger.LogInformation("Seat 2 (Standard 150 UAH): {SeatId}", seat2.Id.Value);
            _logger.LogInformation("Seat 3 (VIP 300 UAH): {SeatId}", seat3.Id.Value);
            _logger.LogInformation("==========================================");
        }
    }
}