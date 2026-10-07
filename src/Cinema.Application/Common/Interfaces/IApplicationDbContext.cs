using System.Data;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cinema.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Hall> Halls { get; }
    DbSet<Seat> Seats { get; }
    DbSet<SeatType> SeatTypes { get; }
    DbSet<Session> Sessions { get; }
    DbSet<Movie> Movies { get; }
    DbSet<Genre> Genres { get; }
    DbSet<PricingItem> PricingItems { get; }
    DbSet<Order> Orders { get; }
    DbSet<User> Users { get; }
    DbSet<Ticket> Tickets { get; }
    DbSet<Pricing> Pricings { get; }
    DbSet<HallTechnology> HallTechnologies { get; } 
    DbSet<Technology> Technologies { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    DatabaseFacade Database { get; }
    Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel serializable, CancellationToken cancellationToken);
    void ClearChangeTracker();
}