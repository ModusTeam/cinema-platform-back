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

public class OrderPaidIntegrationEventHandler(
    IPublishEndpoint publishEndpoint,
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
            await publishEndpoint.Publish(new TicketPurchasedMessage(
                order.Id.Value,
                user.Email,
                $"{user.FirstName} {user.LastName}".Trim(),
                movieTitle,
                sessionDate,
                downloadUrl
            ), cancellationToken);
            
            logger.LogInformation("[OrderPaidIntegrationEventHandler] SUCCESS! Message published to RabbitMQ for Order {OrderId}", order.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[OrderPaidIntegrationEventHandler] FAILED to publish message to RabbitMQ for Order {OrderId}", order.Id);
            throw;
        }
    }
}
