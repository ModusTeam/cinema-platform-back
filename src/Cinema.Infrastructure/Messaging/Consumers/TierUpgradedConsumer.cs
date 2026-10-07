using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Messaging.Consumers;

public class TierUpgradedConsumer(
    IApplicationDbContext context,
    IEmailService emailService,
    ILogger<TierUpgradedConsumer> logger)
    : IConsumer<TierUpgradedMessage>
{
    private const string TierUpgradedPattern = "loyalty.tier_upgraded";

    public async Task Consume(ConsumeContext<TierUpgradedMessage> contextMsg)
    {
        var message = contextMsg.Message;

        if (message.Pattern != TierUpgradedPattern)
            return;

        var payload = message.Data;
        logger.LogInformation(
            "User {UserId} upgraded tier: {OldTier} -> {NewTier}",
            payload.UserId,
            payload.OldTier,
            payload.NewTier);

        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == payload.UserId, contextMsg.CancellationToken);

        if (user == null || string.IsNullOrEmpty(user.Email))
        {
            logger.LogWarning(
                "User {UserId} not found or has no email. Skipping tier upgrade email.",
                payload.UserId);
            return;
        }

        var userName = $"{user.FirstName} {user.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(userName))
            userName = user.Email;

        var emailBody = $@"
            <div style='font-family: Arial, sans-serif; padding: 20px; background-color: #f4f4f4;'>
                <div style='max-width: 600px; margin: 0 auto; background-color: white; padding: 20px; border-radius: 8px; border-top: 4px solid #FFD700;'>
                    <h1 style='color: #333;'>Congratulations, {userName}!</h1>
                    <p style='font-size: 16px;'>Your loyalty tier has just been upgraded to <b>{payload.NewTier}</b>.</p>
                    <p>You can now enjoy more loyalty benefits when buying cinema tickets.</p>
                    <p style='color: #666;'>Thanks for staying with us.</p>
                </div>
            </div>
        ";

        logger.LogInformation("Sending tier upgrade email to {Email}...", user.Email);

        await emailService.SendEmailAsync(
            user.Email,
            $"Your loyalty tier is now {payload.NewTier}",
            emailBody,
            null,
            null);
    }
}
