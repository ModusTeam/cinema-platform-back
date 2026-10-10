using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.EventHandlers;

internal static class OrderEventTickets
{
    public static async Task<List<Ticket>> LoadAsync(
        IApplicationDbContext context, EntityId<Order> orderId, CancellationToken ct)
    {
        List<Ticket> tickets = await context.Tickets
            .Where(t => t.OrderId == orderId)
            .ToListAsync(ct);

        // SavingChanges dispatches events before inserts. Include only tickets awaiting
        // insert; persisted tickets must continue to obey the global query filter.
        HashSet<EntityId<Ticket>> loadedIds = tickets.Select(t => t.Id).ToHashSet();
        foreach (var entry in context.ChangeTracker.Entries<Ticket>())
        {
            Ticket ticket = entry.Entity;
            if (entry.State == EntityState.Added && ticket.OrderId == orderId && loadedIds.Add(ticket.Id))
                tickets.Add(ticket);
        }

        return tickets;
    }
}
