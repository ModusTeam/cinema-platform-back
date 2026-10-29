using System.Reflection;
using Cinema.Catalog.Infrastructure.Persistence.Configurations;
using Cinema.Scheduling.Infrastructure.Persistence.Configurations;
using Cinema.Orders.Infrastructure.Persistence.Configurations;
using Cinema.Tickets.Infrastructure.Persistence.Configurations;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Infrastructure.Persistence.Converters;
using MassTransit;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cinema.Infrastructure.Persistence;

public partial class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>(options), IApplicationDbContext
{
    public DbSet<Movie> Movies { get; init; }
    public DbSet<MovieGenre> MovieGenres { get; init; }
    public DbSet<Genre> Genres { get; init; }
    public DbSet<Hall> Halls { get; init; }
    public DbSet<HallTechnology> HallTechnologies { get; init; }
    public DbSet<Technology> Technologies { get; init; }
    public DbSet<Seat> Seats { get; init; }
    public DbSet<SeatType> SeatTypes { get; init; }
    public DbSet<RefreshToken> RefreshTokens { get; init; }
    public DbSet<Session> Sessions { get; init; }
    public DbSet<Pricing> Pricings { get; init; }
    public DbSet<PricingItem> PricingItems { get; init; }
    public DbSet<Order> Orders { get; init; }
    public DbSet<Ticket> Tickets { get; init; }
    public DbSet<SeatLock> SeatLocks { get; init; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateSessionCompletionWrites();
        ValidateCustomerFactsWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateSessionCompletionWrites();
        ValidateCustomerFactsWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public async Task<IDbContextTransaction> BeginTransactionAsync(System.Data.IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        return await Database.BeginTransactionAsync(isolationLevel, cancellationToken);
    }
    
    public async Task<IDbContextTransaction> BeginHallTransactionAsync(
        EntityId<Hall> hallId, CancellationToken cancellationToken)
    {
        var transaction = await BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            // FOR UPDATE also conflicts with the key-share lock taken by session FK inserts.
            await Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM halls WHERE id = {hallId.Value} FOR UPDATE", cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<DateTimeUtcConverter>();
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MovieConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SessionConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderConfiguration).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TicketConfiguration).Assembly);
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
    
    public void ClearChangeTracker()
    {
        base.ChangeTracker.Clear();
    }
}
