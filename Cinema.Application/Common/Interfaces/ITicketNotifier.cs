using System;
using System.Threading.Tasks;

namespace Cinema.Application.Common.Interfaces;

public interface ITicketNotifier
{
    Task NotifyOrderCompleted(Guid userId, Guid orderId);
    Task NotifyOrderFailed(Guid userId, Guid orderId, string reason);
    Task NotifySeatUnlockedAsync(Guid sessionId, Guid seatId, CancellationToken ct = default);
}