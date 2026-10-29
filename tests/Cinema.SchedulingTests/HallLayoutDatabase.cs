using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Services;
using Cinema.Infrastructure.Persistence;
using Cinema.OrdersTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Cinema.SchedulingTests;

internal sealed class HallLayoutDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    internal string ConnectionString => _connectionString;
    public Hall Hall { get; private set; } = null!;
    public SeatType Standard { get; private set; } = null!;
    public SeatType Premium { get; private set; } = null!;
    public Movie Movie { get; private set; } = null!;
    public Pricing Pricing { get; private set; } = null!;

    private HallLayoutDatabase(string connection, string admin, string database)
        => (_connectionString, _adminConnectionString, _databaseName) = (connection, admin, database);

    public ApplicationDbContext NewContext(params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_connectionString, options => { options.UseVector(); options.EnableRetryOnFailure(3); })
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptors).Options);

    public static async Task<HallLayoutDatabase> CreateAsync(PostgresSchemaTemplate template)
    {
        var (connection, admin, name) = await template.CreateDatabaseAsync();
        HallLayoutDatabase database = new(connection, admin, name);
        try
        {
            await using ApplicationDbContext context = database.NewContext();
            database.Hall = Hall.Create(EntityId<Hall>.New(), "Layout test hall");
            database.Standard = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
            database.Premium = SeatType.New(EntityId<SeatType>.New(), "Premium", null);
            database.Movie = Movie.CreateManual("Test", "Test", 90, DateTime.UtcNow.Year, MovieStatus.ComingSoon);
            database.Pricing = Pricing.New(EntityId<Pricing>.New(), "Test");
            database.Hall.ApplyLayout(new SeatLayoutService().GenerateRectangularGrid(
                2, 3, database.Hall.Id, database.Standard.Id));
            context.Halls.Add(database.Hall);
            context.SeatTypes.AddRange(database.Standard, database.Premium);
            context.Movies.Add(database.Movie);
            context.Pricings.Add(database.Pricing);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public Session NewSession() => Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
        DateTime.UtcNow.AddDays(2).AddHours(2), Movie.Id, Hall.Id, Pricing.Id);

    public async ValueTask DisposeAsync()
    {
        await using NpgsqlConnection admin = new(_adminConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
