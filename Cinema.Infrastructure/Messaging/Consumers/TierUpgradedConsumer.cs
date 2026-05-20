using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Messaging.Consumers;

public class TierUpgradedConsumer(
    IEmailService emailService,
    ILogger<TierUpgradedConsumer> logger) 
    : IConsumer<TierUpgradedMessage>
{
    public async Task Consume(ConsumeContext<TierUpgradedMessage> context)
    {
        var msg = context.Message;
        logger.LogInformation("🌟 User {UserId} upgraded tier: {OldTier} -> {NewTier}", msg.UserId, msg.OldTier, msg.NewTier);

        var emailBody = $@"
            <div style='font-family: Arial, sans-serif; padding: 20px; background-color: #f4f4f4;'>
                <div style='max-width: 600px; margin: 0 auto; background-color: white; padding: 20px; border-radius: 8px; border-top: 4px solid #FFD700;'>
                    <h1 style='color: #333;'>Вітаємо, {msg.UserName}! 🎉</h1>
                    <p style='font-size: 16px;'>Ваш рівень лояльності щойно було підвищено до <b>{msg.NewTier}</b>!</p>
                    <p>Тепер ви отримуєте ще більше кешбеку та нові привілеї при купівлі квитків.</p>
                    <p style='color: #666;'>Дякуємо, що залишаєтесь з нами!</p>
                </div>
            </div>
        ";
        
        logger.LogInformation("📨 Sending tier upgrade email to {Email}...", msg.UserEmail);
        
        await emailService.SendEmailAsync(
            msg.UserEmail, 
            $"Вітаємо з новим рівнем: {msg.NewTier}! 🌟", 
            emailBody, 
            null, 
            null
        );
    }
}