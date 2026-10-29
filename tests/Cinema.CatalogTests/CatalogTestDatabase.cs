using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Infrastructure.Persistence.Configurations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

internal sealed class CatalogTestDatabase : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name;
    public CatalogDbContext Db { get; }
    public IApplicationDbContext Context { get; }

    private CatalogTestDatabase(string admin, string name, CatalogDbContext db)
    {
        _admin = admin;
        _name = name;
        Db = db;
        Context = Substitute.For<IApplicationDbContext>();
        Context.Movies.Returns(db.Set<Movie>());
        Context.Genres.Returns(db.Set<Genre>());
        Context.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => db.SaveChangesAsync(call.Arg<CancellationToken>()));
    }

    public static async Task<CatalogTestDatabase> CreateAsync()
    {
        string? connection = Environment.GetEnvironmentVariable("CINEMA_CATALOG_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Skip("Set CINEMA_CATALOG_TEST_CONNECTION to a local PostgreSQL maintenance connection with CREATEDB permission.");
        NpgsqlConnectionStringBuilder builder = new(connection);
        builder.Host.Should().BeOneOf("localhost", "127.0.0.1");
        builder.Database = "postgres";
        string admin = builder.ConnectionString;
        string name = $"cinema_catalog_test_{Guid.NewGuid():N}";
        await using (NpgsqlConnection db = new(admin))
        {
            await db.OpenAsync();
            await using NpgsqlCommand create = new($"CREATE DATABASE {name}", db);
            await create.ExecuteNonQueryAsync();
        }
        builder.Database = name;
        DbContextOptions<CatalogDbContext> options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(builder.ConnectionString, postgres => postgres.UseVector()).UseSnakeCaseNamingConvention().Options;
        CatalogTestDatabase result = new(admin, name, new CatalogDbContext(options));
        try { await result.Db.Database.EnsureCreatedAsync(); return result; }
        catch { await result.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await using NpgsqlConnection admin = new(_admin);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE {_name} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");
        model.Entity<Movie>().ToTable("movies");
        model.Entity<Genre>().ToTable("genres");
        model.Entity<MovieGenre>().ToTable("movie_genres");
        model.ApplyConfiguration(new MovieConfiguration());
        model.ApplyConfiguration(new GenreConfiguration());
        model.ApplyConfiguration(new MovieGenreConfiguration());
    }
}
