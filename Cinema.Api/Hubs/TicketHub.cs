using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Hubs;

public class TicketHub : Hub<ITicketClient>
{
    public async Task JoinSessionGroup(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId));
    }

    public async Task LeaveSessionGroup(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId));
    }

    public static string GroupName(string sessionId) => $"session:{sessionId}";
}