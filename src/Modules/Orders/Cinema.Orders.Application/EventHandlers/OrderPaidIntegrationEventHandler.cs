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
        logger.LogInformation("OrderPaidIntegrationEventHandler started for Order {OrderId}", notification.Order.Id);
        var order = notification.Order;
        DateTime paidAt = order.PaidAt
            ?? throw new InvalidOperationException("Confirmed order is missing its persisted payment time.");

        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == order.UserId, cancellationToken);

        var session = await context.Sessions
            .Include(s => s.Movie)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == order.SessionId, cancellationToken);

        var movieTitle  = session?.Movie?.Title ?? "Unknown Movie";
        var sessionDate = session?.StartTime    ?? paidAt;
        var eventType   = session?.EventType    ?? "STANDARD";
        string email = user?.Email ?? string.Empty;
        string downloadUrl = string.IsNullOrEmpty(email) ? string.Empty : BuildDownloadUrl(order.Id.Value);
        string userName = user is null ? string.Empty : $"{user.FirstName} {user.LastName}".Trim();

        try
        {
            if (!string.IsNullOrEmpty(email))
            {
                await publishEndpoint.Publish(
                    new TicketPurchasedMessage(
                        order.Id.Value, email, userName,
                        movieTitle, sessionDate, downloadUrl,
                        order.UserId, order.TotalAmount),
                    cancellationToken);
            }
            else
            {
                logger.LogWarning("Skipping email notification for Order {OrderId}: no recipient email.", order.Id);
            }

            var endpoint = await sendEndpointProvider.GetSendEndpoint(
                new Uri("queue:loyalty_ticket_purchased"));

            logger.LogInformation("Sending NestJsTicketPurchasedEvent for Order {OrderId} to loyalty_ticket_purchased", order.Id);

            await endpoint.Send(
                new NestJsTicketPurchasedEvent("TicketPurchased", new NestJsTicketPurchasedDto(
                    EventId:     order.Id.Value,
                    UserId:      order.UserId,
                    OrderId:     order.Id.Value,
                    TotalAmount: (double)order.TotalAmount,
                    TicketAmount:(double)order.TotalAmount,
                    FoodAmount:  0.0,
                    EventType:   eventType,
                    PurchasedAt: paidAt,
                    UserEmail:   email,
                    UserName:    userName,
                    MovieTitle:  movieTitle,
                    SessionDate: sessionDate,
                    DownloadUrl: downloadUrl,
                    PaidAmount:  (double)order.PaidAmount,
                    PointsUsed:  order.PointsUsed,
                    TotalPrice:  (double)order.TotalAmount,
                    SessionId:   order.SessionId.Value,
                    PaidAt:      paidAt)),
                cancellationToken);

            logger.LogInformation("Order {OrderId} published to RabbitMQ.", order.Id);
            logger.LogInformation("NestJsTicketPurchasedEvent sent for Order {OrderId}", order.Id);
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


