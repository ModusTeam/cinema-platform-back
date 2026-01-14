using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Movie> Movies { get; }
    DbSet<Genre> Genres { get; }
    DbSet<Hall> Halls { get; }
    DbSet<Seat> Seats { get; }
    DbSet<SeatType> SeatTypes { get; }
    DbSet<Session> Sessions { get; }
    DbSet<Pricing> Pricings { get; }
    DbSet<PricingItem> PricingItems { get; }
    DbSet<Order> Orders { get; }
    DbSet<Ticket> Tickets { get; }
    DbSet<SeatLock> SeatLocks { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}