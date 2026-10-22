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
            await _hubContext.Clients.Group(TicketHub.GroupName(sessionId.ToString())).SeatLocked(sessionId, seatId, userId), ct
        ).AsTask();
    }

    public Task NotifySeatUnlockedAsync(Guid sessionId, Guid seatId, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(async () => 
            await _hubContext.Clients.Group(TicketHub.GroupName(sessionId.ToString())).SeatUnlocked(sessionId, seatId), ct
        ).AsTask();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        // .NET 10 schedules ExecuteAsync; let it drain before base.StopAsync can cancel its start.
        if (ExecuteTask is { } executeTask)
            await executeTask.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalR Notification Worker started.");
        
        // Completing the writer lets accepted notifications drain during graceful shutdown.
        await foreach (var notificationTask in _channel.Reader.ReadAllAsync(CancellationToken.None))
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
