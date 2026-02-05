using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Hubs;

public interface ITicketClient
{
    Task ReceiveSeatStatusChange(Guid seatId, string status, Guid? userId);
    Task OrderCompleted(Guid orderId);
    Task OrderFailed(object errorData);
}

public class TicketHub : Hub<ITicketClient>
{
    public async Task JoinSessionGroup(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task LeaveSessionGroup(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }
}