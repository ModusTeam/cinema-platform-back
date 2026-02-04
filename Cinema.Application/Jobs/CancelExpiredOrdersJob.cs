using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Jobs;

public class CancelExpiredOrdersJob(
    IApplicationDbContext context,
    ISeatLockingService seatLockingService,
    ILogger<CancelExpiredOrdersJob> logger)
{
    public async Task Process(CancellationToken ct)
    {
        var timeoutThreshold = DateTime.UtcNow.AddMinutes(-15);
        
        var expiredOrdersData = await context.Orders
            .Where(o => o.Status == OrderStatus.Pending && o.BookingDate < timeoutThreshold)
            .Select(o => new { o.Id, o.SessionId, o.UserId })
            .ToListAsync(ct);

        if (!expiredOrdersData.Any()) return;

        logger.LogInformation("Found {Count} expired orders.", expiredOrdersData.Count);
        
        foreach (var orderData in expiredOrdersData)
        {
            try 
            {
                var order = await context.Orders
                    .Include(o => o.Tickets)
                    .FirstOrDefaultAsync(o => o.Id == orderData.Id, ct);

                if (order == null || order.Status != OrderStatus.Pending) continue;

                order.MarkAsCancelled();
                await context.SaveChangesAsync(ct);
                
                var sessionIdValue = orderData.SessionId.Value;

                foreach (var ticket in order.Tickets)
                {
                    await seatLockingService.UnlockSeatAsync(
                        sessionIdValue, 
                        ticket.SeatId.Value, 
                        orderData.UserId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to cancel order {OrderId}", orderData.Id);
            }
        }
    }
}