using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Queries;

internal static class OrderHistoryTickets
{
    public static async Task PopulateAsync(IApplicationDbContext context, List<OrderDto> orders, CancellationToken ct)
    {
        if (orders.Count == 0)
            return;

        EntityId<Order>[] orderIds = orders.Select(order => new EntityId<Order>(order.Id)).ToArray();
        var tickets = await context.Tickets
            .AsNoTracking()
            .Where(ticket => orderIds.Contains(ticket.OrderId))
            .Include(ticket => ticket.Session)!.ThenInclude(session => session!.Movie)
            .Include(ticket => ticket.Session)!.ThenInclude(session => session!.Hall)
            .Include(ticket => ticket.Seat)!.ThenInclude(seat => seat!.SeatType)
            .ToListAsync(ct);

        var ticketsByOrder = tickets.ToLookup(ticket => ticket.OrderId.Value, ticket => ticket.Adapt<TicketDto>());
        foreach (OrderDto order in orders)
            order.Tickets = ticketsByOrder[order.Id].ToList();
    }
}
