using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;

namespace Cinema.OrdersTests;

internal static class OrderTicketFixture
{
    public static (Order Order, List<Ticket> Tickets) Create(
        Guid userId, Session session, List<Seat> seats, Dictionary<EntityId<Seat>, decimal> prices)
    {
        Order order = Order.CreateWithoutTickets(userId, session, seats, prices);
        List<Ticket> tickets = seats.Select(seat => Ticket.New(
            EntityId<Ticket>.New(), prices[seat.Id], TicketStatus.Valid,
            order.Id, session.Id, seat.Id)).ToList();
        return (order, tickets);
    }

    public static Order CreateAndTrack(
        ApplicationDbContext context, Guid userId, Session session, List<Seat> seats,
        Dictionary<EntityId<Seat>, decimal> prices)
    {
        (Order order, List<Ticket> tickets) = Create(userId, session, seats, prices);
        context.Orders.Add(order);
        context.Tickets.AddRange(tickets);
        return order;
    }

    public static IQueryable<Ticket> ForOrder(ApplicationDbContext context, Order order) =>
        context.Tickets.Where(ticket => ticket.OrderId == order.Id);

    public static IEnumerable<Ticket> TrackedForOrder(ApplicationDbContext context, Order order) =>
        context.Tickets.Local.Where(ticket => ticket.OrderId == order.Id);
}
