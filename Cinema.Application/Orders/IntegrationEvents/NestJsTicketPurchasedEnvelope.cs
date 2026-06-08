using System.Text.Json.Serialization;

namespace Cinema.Application.Orders.IntegrationEvents;

public record NestJsTicketPurchasedDto(
    [property: JsonPropertyName("eventId")]    Guid     EventId,
    [property: JsonPropertyName("userId")]     Guid     UserId,
    [property: JsonPropertyName("orderId")]    Guid     OrderId,
    [property: JsonPropertyName("totalAmount")]  double TotalAmount,
    [property: JsonPropertyName("ticketAmount")] double TicketAmount,
    [property: JsonPropertyName("foodAmount")]   double FoodAmount,
    [property: JsonPropertyName("eventType")]  string   EventType,
    [property: JsonPropertyName("purchasedAt")] DateTime PurchasedAt,
    [property: JsonPropertyName("userEmail")]  string   UserEmail,
    [property: JsonPropertyName("userName")]   string   UserName,
    [property: JsonPropertyName("movieTitle")] string   MovieTitle,
    [property: JsonPropertyName("sessionDate")] DateTime SessionDate,
    [property: JsonPropertyName("downloadUrl")] string  DownloadUrl,
    [property: JsonPropertyName("paidAmount")]  double  PaidAmount,
    [property: JsonPropertyName("pointsUsed")]  int     PointsUsed,
    [property: JsonPropertyName("totalPrice")]  double  TotalPrice
);

public record NestJsTicketPurchasedEvent(
    [property: JsonPropertyName("pattern")] string                   Pattern,
    [property: JsonPropertyName("data")]    NestJsTicketPurchasedDto Data
);


