// File: Cinema.Domain/Services/OrderingDomainService.cs
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;

namespace Cinema.Domain.Services;

public class OrderingDomainService
{
    public (Order Order, List<Ticket> Tickets) CreateOrderAggregate(
        User user, 
        Session session, 
        List<Seat> seats, 
        Dictionary<EntityId<Seat>, decimal> prices)
    {
        if (session.StartTime <= DateTime.UtcNow)
        {
            throw new DomainException("Cannot book tickets for a session that has already started.");
        }

        if (seats.Any(s => s.HallId != session.HallId))
        {
            throw new DomainException("One or more seats do not belong to the session's hall.");
        }
        
        var unavailableSeat = seats.FirstOrDefault(s => s.Status != SeatStatus.Active);
        if (unavailableSeat != null)
        {
            throw new DomainException($"Seat {unavailableSeat.RowLabel}-{unavailableSeat.Number} is currently unavailable.");
        }
        
        decimal totalAmount = 0;
        foreach (var seat in seats)
        {
            if (!prices.TryGetValue(seat.Id, out var price))
            {
                throw new DomainException($"Price not configured for seat {seat.RowLabel}-{seat.Number}.");
            }
            totalAmount += price;
        }

        var orderId = EntityId<Order>.New();

        var order = Order.New(orderId, totalAmount, user.Id, session.Id);

        var tickets = new List<Ticket>();
        foreach (var seat in seats)
        {
            var ticket = Ticket.New(
                EntityId<Ticket>.New(),
                prices[seat.Id],
                TicketStatus.Valid,
                orderId,
                session.Id,
                seat.Id
            );
            tickets.Add(ticket);
        }

        return (order, tickets);
    }
}