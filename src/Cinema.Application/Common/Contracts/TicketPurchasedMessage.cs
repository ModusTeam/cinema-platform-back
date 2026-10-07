namespace Cinema.Application.Common.Contracts;

public record TicketPurchasedMessage(
    Guid OrderId,
    string UserEmail,
    string UserName,
    string MovieTitle,
    DateTime SessionDate,
    string DownloadUrl,
    Guid UserId,
    decimal TotalPrice
);