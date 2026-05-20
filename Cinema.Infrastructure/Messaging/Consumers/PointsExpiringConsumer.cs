using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Messaging.Consumers;

public class PointsExpiringConsumer(
    IApplicationDbContext context,
    IEmailService emailService,
    ILogger<PointsExpiringConsumer> logger) : IConsumer<PointsExpiringMessage>
{
    public async Task Consume(ConsumeContext<PointsExpiringMessage> contextMsg)
    {
        var message = contextMsg.Message;

        if (message.Pattern != "loyalty.points_expiring")
            return;

        var payload = message.Data;
        logger.LogInformation("Отримано запит на сповіщення про згорання {Points} балів для User {UserId}", payload.Points, payload.UserId);
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == payload.UserId, contextMsg.CancellationToken);

        if (user == null || string.IsNullOrEmpty(user.Email))
        {
            logger.LogWarning("Користувача {UserId} не знайдено або відсутній Email.", payload.UserId);
            return;
        }

        var subject = "Ваші бали лояльності скоро згорять! 🍿";
        var body = $"""
            Привіт, {user.FirstName}!
            
            Нагадуємо, що ваші {payload.Points} бонусних балів згорять {payload.ExpiresAt:dd.MM.yyyy}. 
            Встигніть обміняти їх на безкоштовний попкорн або знижку на квиток на найближчий сеанс!
            
            З любов'ю,
            Ваш кінотеатр.
            """;
            
        await emailService.SendEmailAsync(user.Email, subject, body);
        logger.LogInformation("Сповіщення успішно відправлено на {Email}", user.Email);
    }
}