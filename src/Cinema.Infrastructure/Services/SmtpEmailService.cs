using System.Net;
using System.Net.Mail;
using Cinema.Application.Common.Interfaces;
using Cinema.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinema.Infrastructure.Services;

public class SmtpEmailService(IOptions<SmtpSettings> options, ILogger<SmtpEmailService> logger) : IEmailService
{
    private readonly SmtpSettings _settings = options.Value;

    public async Task SendEmailAsync(string to, string subject, string body, byte[]? attachment = null, string? attachmentName = null)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogWarning("SendEmailAsync skipped: recipient address is empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.SenderEmail))
        {
            logger.LogWarning("SendEmailAsync skipped: SmtpSettings.SenderEmail is not configured.");
            return;
        }

        try
        {
            using var client = new SmtpClient(_settings.Host, _settings.Port)
            {
                Credentials = new NetworkCredential(_settings.Username, _settings.Password),
                EnableSsl = true
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(_settings.SenderEmail, "Cinema Platform"),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            mailMessage.To.Add(to);

            if (attachment != null && !string.IsNullOrEmpty(attachmentName))
            {
                mailMessage.Attachments.Add(new Attachment(new MemoryStream(attachment), attachmentName));
            }

            await client.SendMailAsync(mailMessage);
            logger.LogInformation("Email sent to {Email}, attachment: {HasAttachment}", to, attachment != null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email to {Email}", to);
        }
    }
}