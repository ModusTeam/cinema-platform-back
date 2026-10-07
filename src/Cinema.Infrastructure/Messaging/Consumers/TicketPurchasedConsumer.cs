using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Messaging.Consumers;

public class TicketPurchasedConsumer(
    IEmailService emailService, 
    ITicketGenerator ticketGenerator,
    ILogger<TicketPurchasedConsumer> logger) 
    : IConsumer<TicketPurchasedMessage>
{
    public async Task Consume(ConsumeContext<TicketPurchasedMessage> context)
    {
        var msg = context.Message;
        logger.LogInformation("🎫 Generating ticket PDF for Order {OrderId}...", msg.OrderId);

        var pdfBytes = ticketGenerator.GenerateTicketPdf(msg);
        var fileName = $"ticket_{msg.OrderId}.pdf";

        var emailBody = $@"
            <div style='font-family: Arial, sans-serif; padding: 20px; background-color: #f4f4f4;'>
                <div style='max-width: 600px; margin: 0 auto; background-color: white; padding: 20px; border-radius: 8px;'>
                    <h1 style='color: #333;'>Hello {msg.UserName}!</h1>
                    <p style='font-size: 16px;'>Thank you for your purchase.</p>
                    <p>Your tickets for <b>{msg.MovieTitle}</b> are attached to this email.</p>
                    <p style='color: #666;'>See you at the cinema!</p>
                </div>
            </div>
        ";
        
        logger.LogInformation("📨 Sending email with PDF attachment to {Email}...", msg.UserEmail);
        
        await emailService.SendEmailAsync(
            msg.UserEmail, 
            $"Your Tickets - {msg.MovieTitle}", 
            emailBody, 
            pdfBytes, 
            fileName
        );
    }
}