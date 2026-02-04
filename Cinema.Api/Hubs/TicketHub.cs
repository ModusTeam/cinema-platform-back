using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Hubs;

public class TicketHub : Hub
{
    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }
}