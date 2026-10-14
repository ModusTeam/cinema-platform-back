using Cinema.Application.Common.Constants;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Jobs;

public class CancelExpiredOrdersJob(
    IApplicationDbContext context,
    ILogger<CancelExpiredOrdersJob> logger)
{
    private const int BatchSize = 100;

    public async Task Process(CancellationToken ct)
    {
        logger.LogInformation("Starting CancelExpiredOrdersJob...");
        
        var timeoutThreshold = DateTime.UtcNow.AddMinutes(-OrderConstants.SeatLockDurationMinutes);

        bool hasMore = true;
        int totalProcessed = 0;

        while (hasMore)
        {
            var expiredOrdersBatch = await context.Orders
                .Where(o => o.Status == OrderStatus.Pending && o.BookingDate < timeoutThreshold)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (expiredOrdersBatch.Count == 0)
            {
                hasMore = false;
                break;
            }

            var orderIds = expiredOrdersBatch.Select(o => o.Id).ToList();
            var ticketsByOrder = (await context.Tickets.IgnoreQueryFilters()
                    .Where(t => orderIds.Contains(t.OrderId) && t.TicketStatus == TicketStatus.Valid)
                    .ToListAsync(ct))
                .GroupBy(t => t.OrderId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var order in expiredOrdersBatch)
            {
                try
                {
                    if (ticketsByOrder.TryGetValue(order.Id, out var tickets))
                    {
                        foreach (Ticket ticket in tickets)
                            ticket.MarkAsRefunded();
                    }

                    order.MarkAsCancelled();
                    
                    logger.LogInformation("Marked order {OrderId} as cancelled (expired).", order.Id.Value);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process expiration for order {OrderId}", order.Id.Value);
                }
            }
            
            await context.SaveChangesAsync(ct);
            
            totalProcessed += expiredOrdersBatch.Count;

            context.ClearChangeTracker();
        }

        if (totalProcessed > 0)
        {
            logger.LogInformation("CancelExpiredOrdersJob completed. Total cancelled: {Count}", totalProcessed);
        }
    }
}
