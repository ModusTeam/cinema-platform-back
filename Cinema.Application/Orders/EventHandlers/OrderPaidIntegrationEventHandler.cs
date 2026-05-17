using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Domain.Events;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinema.Application.Orders.EventHandlers;

public record NestJsTicketPurchasedDto(
    Guid EventId,
    Guid UserId,
    Guid OrderId,
    double TotalAmount,
    double TicketAmount,
    double FoodAmount,
    string EventType,
    DateTime PurchasedAt,
    string UserEmail,
    string UserName,
    string MovieTitle,
    DateTime SessionDate,
    string DownloadUrl,
    double TotalPrice
);

public record NestJsTicketPurchasedEvent(string Pattern, NestJsTicketPurchasedDto Data);

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
        logger.LogInformation("[OrderPaidIntegrationEventHandler] START handling OrderPaidEvent for OrderId: {OrderId}", notification.Order.Id);

        var order = notification.Order;
        
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == order.UserId, cancellationToken);

        if (user == null || string.IsNullOrEmpty(user.Email))
        {
            logger.LogWarning("[OrderPaidIntegrationEventHandler] User {UserId} not found or no email. Aborting.", order.UserId);
            return; 
        }

        var session = await context.Sessions
            .Include(s => s.Movie)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == order.SessionId, cancellationToken);
            
        var movieTitle = session?.Movie?.Title ?? "Unknown Movie";
        var sessionDate = session?.StartTime ?? DateTime.UtcNow;

        var relativePath = string.Format(_settings.TicketDownloadPath, order.Id.Value);
        var downloadUrl = $"{_settings.BaseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";

        logger.LogInformation("[OrderPaidIntegrationEventHandler] Preparing message for {Email}. Movie: {Movie}", user.Email, movieTitle);

        try 
        {
            var message = new TicketPurchasedMessage(
                order.Id.Value,
                user.Email,
                $"{user.FirstName} {user.LastName}".Trim(),
                movieTitle,
                sessionDate,
                downloadUrl,
                order.UserId, 
                order.TotalAmount
            );

            // Publish for internal C# consumers (Email Service)
            await publishEndpoint.Publish(message, cancellationToken);
            
            // Send specifically formatted raw message to NestJS queue
            logger.LogInformation("[OrderPaidIntegrationEventHandler] Connecting to NestJS queue 'loyalty_ticket_purchased'...");
            var nestJsEndpoint = await sendEndpointProvider.GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
            
            var nestJsData = new NestJsTicketPurchasedDto(
                EventId: Guid.NewGuid(),
                UserId: order.UserId,
                OrderId: order.Id.Value,
                TotalAmount: (double)order.TotalAmount,
                TicketAmount: (double)order.TotalAmount,
                FoodAmount: 0.0,
                EventType: "STANDARD",
                PurchasedAt: DateTime.UtcNow,
                UserEmail: user.Email,
                UserName: $"{user.FirstName} {user.LastName}".Trim(),
                MovieTitle: movieTitle,
                SessionDate: sessionDate,
                DownloadUrl: downloadUrl,
                TotalPrice: (double)order.TotalAmount
            );

            var payload = new NestJsTicketPurchasedEvent("TicketPurchased", nestJsData);

            logger.LogInformation("[OrderPaidIntegrationEventHandler] Sending RAW JSON to NestJS queue...");
            await nestJsEndpoint.Send(payload, cancellationToken);
            logger.LogInformation("[OrderPaidIntegrationEventHandler] Successfully sent RAW JSON to NestJS queue.");

            logger.LogInformation("[OrderPaidIntegrationEventHandler] SUCCESS! Message published to RabbitMQ for Order {OrderId}", order.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[OrderPaidIntegrationEventHandler] FAILED to publish message to RabbitMQ for Order {OrderId}", order.Id);
            throw;
        }
    }
}
