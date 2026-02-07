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
        var order = notification.Order;
        
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == order.UserId, cancellationToken);

        if (user == null || string.IsNullOrEmpty(user.Email))
        {
            logger.LogWarning("Cannot send ticket email: User {UserId} not found or no email", order.UserId);
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

        logger.LogInformation("Publishing TicketPurchasedMessage for Order {OrderId} to {Email}", order.Id, user.Email);

        await publishEndpoint.Publish(new TicketPurchasedMessage(
            order.Id.Value,
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            movieTitle,
            sessionDate,
            downloadUrl
        ), cancellationToken);
    }
}
