using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Hubs;


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
    
    public static string GroupName(string sessionId) => $"session:{sessionId}";
}