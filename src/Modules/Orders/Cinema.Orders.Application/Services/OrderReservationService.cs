using System.Data;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinema.Application.Services;

public class OrderReservationService(
    IApplicationDbContext context,
    IPriceCalculator priceCalculator) : IOrderReservationService
{
    public async Task<Result<Guid>> ReserveOrderAsync(Guid userId, Guid sessionId, List<Guid> seatIds, CancellationToken ct)
    {
        if (seatIds.Count == 0 || seatIds.Count != seatIds.Distinct().Count())
            return Result.Failure<Guid>(new Error("Order.SeatsNotFound", "Mismatch in seat availability."));

        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await context.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
                // Arbitration with lifecycle: Session SHARE -> Seats. No Hall lock here.
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT id FROM sessions WHERE id = {sessionId} FOR SHARE", ct);
                var rawSeatIds = seatIds.ToArray();
                
                var seats = await context.Seats
                    .FromSql($"SELECT * FROM seats WHERE id = ANY({rawSeatIds}) FOR UPDATE NOWAIT")
                    .ToListAsync(ct);

                if (seats.Count != seatIds.Count)
                    return Result.Failure<Guid>(new Error("Order.SeatsNotFound", "Mismatch in seat availability."));

                if (seats.Any(s => s.Status != SeatStatus.Active))
                    return Result.Failure<Guid>(new Error("Order.SeatsNotActive", "Seats not active."));
                
                var targetSeatIds = seatIds.Select(id => new EntityId<Seat>(id)).ToList();
                var areSeatsSold = await context.Tickets
                    .AnyAsync(t => targetSeatIds.Contains(t.SeatId) && 
                                   t.SessionId == new EntityId<Session>(sessionId) &&
                                   (t.TicketStatus == TicketStatus.Reserved || t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used), ct);
                 
                if (areSeatsSold) return Result.Failure<Guid>(new Error("Order.SeatsSold", "Seats already sold."));
                 
                var session = await context.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == new EntityId<Session>(sessionId), ct);
                if (session == null) return Result.Failure<Guid>(new Error("Session.NotFound", "Session not found"));
                if (session.StartTime <= DateTime.UtcNow ||
                    session.Status is SessionStatus.Cancelled or SessionStatus.Ended or SessionStatus.SoldOut)
                    return Result.Failure<Guid>(new Error("Session.NotAvailable", "Session is not available for booking."));
                if (seats.Any(seat => seat.HallId != session.HallId))
                    return Result.Failure<Guid>(new Error("Order.SeatsNotFound", "Mismatch in seat availability."));

                var pricing = await context.Pricings.Include(p => p.PricingItems).AsNoTracking().FirstOrDefaultAsync(p => p.Id == session.PricingId, ct);
                if (pricing == null) return Result.Failure<Guid>(new Error("Pricing.NotFound", "Pricing not found"));
                
                var prices = new Dictionary<EntityId<Seat>, decimal>();
                foreach (var seat in seats)
                {
                    prices.Add(seat.Id, priceCalculator.CalculatePrice(pricing, seat.SeatTypeId, session.StartTime));
                }
                
                Order order = Order.CreateWithoutTickets(userId, session, seats, prices);
                List<Ticket> tickets = seats.Select(seat => Ticket.Reserve(
                    EntityId<Ticket>.New(), prices[seat.Id],
                    order.Id, session.Id, seat.Id)).ToList();
                context.Orders.Add(order);
                context.Tickets.AddRange(tickets);
                await context.SaveChangesAsync(ct);
                
                await transaction.CommitAsync(ct);
                
                return Result.Success(order.Id.Value);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres &&
                postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                postgres.ConstraintName == "ix_tickets_session_id_seat_id")
            {
                await transaction.RollbackAsync(ct);
                return Result.Failure<Guid>(new Error("Order.SeatsSold", "Seats already sold."));
            }
            catch (InvalidOperationException exception) when (exception.InnerException is PostgresException postgres &&
                postgres.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                await transaction.RollbackAsync(ct);
                return Result.Failure<Guid>(new Error("Order.SeatsUnavailable", "Seats are being reserved."));
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }
}
