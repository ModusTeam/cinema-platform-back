using System.Reflection;
using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Movie> Movies { get; init; }
    public DbSet<MovieGenre> MovieGenres { get; init; }
    public DbSet<Genre> Genres { get; init; }
    public DbSet<Hall> Halls { get; init; }
    public DbSet<HallTechnology> HallTechnologies { get; init; }
    public DbSet<Technology> Technologies { get; init; }
    public DbSet<Seat> Seats { get; init; }
    public DbSet<SeatType> SeatTypes { get; init; }
    public DbSet<Session> Sessions { get; init; }
    public DbSet<Pricing> Pricings { get; init; }
    public DbSet<PricingItem> PricingItems { get; init; }
    public DbSet<Order> Orders { get; init; }
    public DbSet<Ticket> Tickets { get; init; }
    public DbSet<SeatLock> SeatLocks { get; init; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }
}