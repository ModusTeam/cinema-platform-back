using System;
using System.Threading;
using System.Threading.Tasks;
using Cinema.Api.Hubs;
using Cinema.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Services;

public class SignalRTicketNotifier(IHubContext<TicketHub> hubContext) : ITicketNotifier
{
    public async Task NotifyOrderCompleted(Guid userId, Guid orderId)
    {
        await hubContext.Clients.User(userId.ToString()).SendAsync("OrderCompleted", orderId);
    }

    public async Task NotifyOrderFailed(Guid userId, Guid orderId, string reason)
    {
        await hubContext.Clients.User(userId.ToString()).SendAsync("OrderFailed", new { OrderId = orderId, Reason = reason });
    }

    public async Task NotifySeatUnlockedAsync(Guid sessionId, Guid seatId, CancellationToken ct = default)
    {
        await hubContext.Clients.Group(sessionId.ToString())
            .SendAsync("SeatUnlocked", seatId, cancellationToken: ct);
    }
}