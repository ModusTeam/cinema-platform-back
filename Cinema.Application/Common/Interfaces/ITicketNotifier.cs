namespace Cinema.Application.Common.Interfaces;

public interface ITicketNotifier
{
    Task NotifySeatLockedAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default);
    Task NotifySeatUnlockedAsync(Guid sessionId, Guid seatId, CancellationToken ct = default);
}