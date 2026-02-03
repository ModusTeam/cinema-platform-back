using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Hubs;

public interface ITicketClient
{
    Task SeatLocked(Guid seatId, Guid userId);
    Task SeatUnlocked(Guid seatId);
}

public class TicketHub : Hub<ITicketClient>
{
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }
}