using System.Data;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Services;

public class OrderReservationService(
    IApplicationDbContext context,
    IPriceCalculator priceCalculator) : IOrderReservationService
{
    public async Task<Result<Guid>> ReserveOrderAsync(Guid userId, Guid sessionId, List<Guid> seatIds, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await context.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
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
                                   (t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used), ct);
                 
                if (areSeatsSold) return Result.Failure<Guid>(new Error("Order.SeatsSold", "Seats already sold."));
                 
                var session = await context.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == new EntityId<Session>(sessionId), ct);
                if (session == null) return Result.Failure<Guid>(new Error("Session.NotFound", "Session not found"));

                var pricing = await context.Pricings.Include(p => p.PricingItems).AsNoTracking().FirstOrDefaultAsync(p => p.Id == session.PricingId, ct);
                if (pricing == null) return Result.Failure<Guid>(new Error("Pricing.NotFound", "Pricing not found"));
                
                var prices = new Dictionary<EntityId<Seat>, decimal>();
                foreach (var seat in seats)
                {
                    prices.Add(seat.Id, priceCalculator.CalculatePrice(pricing, seat.SeatTypeId, session.StartTime));
                }
                
                var order = Order.Create(userId, session, seats, prices);
                context.Orders.Add(order);
                await context.SaveChangesAsync(ct);
                
                await transaction.CommitAsync(ct);
                
                return Result.Success(order.Id.Value);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }
}