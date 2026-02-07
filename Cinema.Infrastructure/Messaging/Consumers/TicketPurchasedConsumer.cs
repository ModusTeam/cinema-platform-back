using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Messaging.Consumers;

public class TicketPurchasedConsumer(IEmailService emailService, ILogger<TicketPurchasedConsumer> logger) 
    : IConsumer<TicketPurchasedMessage>
{
    public async Task Consume(ConsumeContext<TicketPurchasedMessage> context)
    {
        var msg = context.Message;
        logger.LogInformation("Processing ticket notification for Order {OrderId}", msg.OrderId);

        var emailBody = $@"
            <h1>Hello {msg.UserName},</h1>
            <p>You have successfully purchased tickets for <b>{msg.MovieTitle}</b>.</p>
            <p>Session Date: {msg.SessionDate:f}</p>
            <hr/>
            <p><a href='{msg.TicketDownloadUrl}'>Download Tickets</a></p>
        ";

        await emailService.SendEmailAsync(msg.UserEmail, "Your Movie Tickets", emailBody);
    }
}