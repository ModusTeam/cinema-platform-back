namespace Cinema.Application.Orders.Dtos;

public record TicketDto(
    Guid Id,
    string MovieTitle,
    string PosterUrl,
    string HallName,
    string RowLabel,
    int SeatNumber,
    string SeatType,
    decimal Price,
    DateTime SessionStart,
    string Status,
    string SecretCode 
);