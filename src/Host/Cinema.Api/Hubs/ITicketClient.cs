namespace Cinema.Api.Hubs;

public interface ITicketClient
{
    Task ReceiveSeatStatusChange(Guid seatId, string status, Guid? userId);
    Task OrderCompleted(Guid orderId);
    Task OrderFailed(object errorData);
    Task SeatLocked(Guid sessionId, Guid seatId, Guid userId);
    Task SeatUnlocked(Guid sessionId, Guid seatId);
}