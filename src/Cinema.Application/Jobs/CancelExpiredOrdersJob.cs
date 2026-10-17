using System.Data;
using Cinema.Application.Common.Constants;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Jobs;

public class CancelExpiredOrdersJob(
    IApplicationDbContext context,
    ISeatLockingService seatLockingService,
    ITicketNotifier ticketNotifier,
    ILogger<CancelExpiredOrdersJob> logger)
{
    private const int BatchSize = 100;

    public async Task Process(CancellationToken ct)
    {
        DateTime timeoutThreshold = DateTime.UtcNow.AddMinutes(-OrderConstants.SeatLockDurationMinutes);
        int totalProcessed = 0;

        while (true)
        {
            // Pending/Valid is a legacy ambiguity: preserve it for payment reconciliation.
            List<EntityId<Order>> orderIds = await context.Orders.AsNoTracking()
                .Where(order => order.Status == OrderStatus.Pending && order.BookingDate < timeoutThreshold &&
                    context.Tickets.IgnoreQueryFilters().Any(ticket => ticket.OrderId == order.Id && ticket.TicketStatus == TicketStatus.Reserved) &&
                    !context.Tickets.IgnoreQueryFilters().Any(ticket => ticket.OrderId == order.Id && ticket.TicketStatus != TicketStatus.Reserved))
                .OrderBy(order => order.BookingDate)
                .Take(BatchSize)
                .Select(order => order.Id)
                .ToListAsync(ct);

            if (orderIds.Count == 0)
                break;

            foreach (EntityId<Order> id in orderIds)
            {
                if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
                {
                    Order? inMemoryOrder = await context.Orders.FindAsync([id], ct);
                    if (inMemoryOrder?.Status != OrderStatus.Pending)
                        continue;
                    inMemoryOrder.MarkAsExpired();
                    List<Ticket> inMemoryTickets = await context.Tickets.IgnoreQueryFilters()
                        .Where(ticket => ticket.OrderId == id).ToListAsync(ct);
                    foreach (Ticket ticket in inMemoryTickets)
                        ticket.ReleaseReservation(inMemoryOrder);
                    await context.SaveChangesAsync(ct);
                    totalProcessed++;
                    context.ClearChangeTracker();
                    await NotifyReleasedSeatsAsync(inMemoryOrder, inMemoryTickets, ct);
                    continue;
                }

                await using var transaction = await context.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                int claimed = await context.Orders
                    .Where(order => order.Id == id && order.Status == OrderStatus.Pending && order.BookingDate < timeoutThreshold)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Status, OrderStatus.Expired), ct);
                if (claimed == 0)
                {
                    await transaction.RollbackAsync(ct);
                    continue;
                }

                context.ClearChangeTracker();
                Order expiredOrder = await context.Orders.SingleAsync(order => order.Id == id, ct);
                List<Ticket> tickets = await context.Tickets.IgnoreQueryFilters()
                    .Where(ticket => ticket.OrderId == id).ToListAsync(ct);
                foreach (Ticket ticket in tickets)
                    ticket.ReleaseReservation(expiredOrder);

                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                totalProcessed++;
                context.ClearChangeTracker();
                await NotifyReleasedSeatsAsync(expiredOrder, tickets, ct);
            }

            if (orderIds.Count < BatchSize)
                break;
        }

        logger.LogInformation("CancelExpiredOrdersJob completed. Total expired: {Count}", totalProcessed);
    }

    private async Task NotifyReleasedSeatsAsync(Order order, List<Ticket> tickets, CancellationToken ct)
    {
        List<Guid> seatIds = tickets.Select(ticket => ticket.SeatId.Value).ToList();
        try
        {
            await seatLockingService.UnlockSeatsAsync(order.SessionId.Value, seatIds, order.UserId, ct, notifyClients: false);
            foreach (Guid seatId in seatIds)
                await ticketNotifier.NotifySeatUnlockedAsync(order.SessionId.Value, seatId, ct);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Seat release notification failed for expired order {OrderId}", order.Id.Value);
        }
    }
}
