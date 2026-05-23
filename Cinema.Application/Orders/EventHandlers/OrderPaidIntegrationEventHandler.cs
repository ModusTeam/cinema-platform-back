using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Domain.Events;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinema.Application.Orders.EventHandlers;

public class OrderPaidIntegrationEventHandler(
    IPublishEndpoint publishEndpoint,
    ISendEndpointProvider sendEndpointProvider,
    IApplicationDbContext context,
    IOptions<FrontendSettings> frontendSettings,
    ILogger<OrderPaidIntegrationEventHandler> logger)
    : INotificationHandler<OrderPaidEvent>
{
    private readonly FrontendSettings _settings = frontendSettings.Value;

    public async Task Handle(OrderPaidEvent notification, CancellationToken cancellationToken)
    {
        var order = notification.Order;

        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == order.UserId, cancellationToken);

        if (user is null || string.IsNullOrEmpty(user.Email))
        {
            logger.LogWarning("User {UserId} not found or has no email. Skipping RabbitMQ publish for Order {OrderId}.",
                order.UserId, order.Id);
            return;
        }

        var session = await context.Sessions
            .Include(s => s.Movie)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == order.SessionId, cancellationToken);

        var movieTitle  = session?.Movie?.Title ?? "Unknown Movie";
        var sessionDate = session?.StartTime    ?? DateTime.UtcNow;
        var eventType   = session?.EventType    ?? "STANDARD";
        var downloadUrl = BuildDownloadUrl(order.Id.Value);
        var userName    = $"{user.FirstName} {user.LastName}".Trim();

        try
        {
            await publishEndpoint.Publish(
                new TicketPurchasedMessage(
                    order.Id.Value, user.Email, userName,
                    movieTitle, sessionDate, downloadUrl,
                    order.UserId, order.TotalAmount),
                cancellationToken);

            var endpoint = await sendEndpointProvider.GetSendEndpoint(
                new Uri("queue:loyalty_ticket_purchased"));

            await endpoint.Send(
                new NestJsTicketPurchasedEvent("TicketPurchased", new NestJsTicketPurchasedDto(
                    EventId:     order.Id.Value,
                    UserId:      order.UserId,
                    OrderId:     order.Id.Value,
                    TotalAmount: (double)order.PaidAmount,
                    TicketAmount:(double)order.PaidAmount,
                    FoodAmount:  0.0,
                    EventType:   eventType,
                    PurchasedAt: DateTime.UtcNow,
                    UserEmail:   user.Email,
                    UserName:    userName,
                    MovieTitle:  movieTitle,
                    SessionDate: sessionDate,
                    DownloadUrl: downloadUrl,
                    TotalPrice:  (double)order.PaidAmount)),
                cancellationToken);

            logger.LogInformation("Order {OrderId} published to RabbitMQ.", order.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish RabbitMQ messages for Order {OrderId}.", order.Id);
            throw;
        }
    }

    private string BuildDownloadUrl(Guid orderId)
    {
        var relative = string.Format(_settings.TicketDownloadPath, orderId);
        return $"{_settings.BaseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";
    }
}
