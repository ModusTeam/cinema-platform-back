using Cinema.Api.Hubs;
using Cinema.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Services;

public class SignalRTicketNotifier(IHubContext<TicketHub, ITicketClient> hubContext) : ITicketNotifier
{
    public async Task NotifySeatLockedAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        await hubContext.Clients.Group(sessionId.ToString())
            .SeatLocked(seatId, userId);
    }

    public async Task NotifySeatUnlockedAsync(Guid sessionId, Guid seatId, CancellationToken ct = default)
    {
        await hubContext.Clients.Group(sessionId.ToString())
            .SeatUnlocked(seatId);
    }
}