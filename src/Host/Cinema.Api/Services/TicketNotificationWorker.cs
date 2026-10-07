using System.Threading.Channels;
using Cinema.Api.Hubs;
using Cinema.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Cinema.Api.Services;

public class TicketNotificationWorker : BackgroundService, ITicketNotifier
{
    private readonly IHubContext<TicketHub, ITicketClient> _hubContext;
    private readonly ILogger<TicketNotificationWorker> _logger;
    private readonly Channel<Func<Task>> _channel;

    public TicketNotificationWorker(
        IHubContext<TicketHub, ITicketClient> hubContext,
        ILogger<TicketNotificationWorker> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
        _channel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    }

    public Task NotifyOrderCompleted(Guid userId, Guid orderId)
    {
        return _channel.Writer.WriteAsync(async () => 
            await _hubContext.Clients.User(userId.ToString()).OrderCompleted(orderId)
        ).AsTask();
    }

    public Task NotifyOrderFailed(Guid userId, Guid orderId, string reason)
    {
        return _channel.Writer.WriteAsync(async () => 
            await _hubContext.Clients.User(userId.ToString()).OrderFailed(new { OrderId = orderId, Reason = reason })
        ).AsTask();
    }

    public Task NotifySeatLockedAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(async () => 
            await _hubContext.Clients.Group(sessionId.ToString()).ReceiveSeatStatusChange(seatId, "Locked", userId), ct
        ).AsTask();
    }

    public Task NotifySeatUnlockedAsync(Guid sessionId, Guid seatId, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(async () => 
            await _hubContext.Clients.Group(sessionId.ToString()).ReceiveSeatStatusChange(seatId, "Available", null), ct
        ).AsTask();
    }

    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalR Notification Worker started.");
        
        await foreach (var notificationTask in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await notificationTask();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SignalR notification.");
            }
        }
    }
}