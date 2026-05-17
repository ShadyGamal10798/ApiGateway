namespace HealthNotifier;

using System.Net;
using System.Net.Mail;

public sealed class EmailNotificationSender(ILogger<EmailNotificationSender> logger)
{
    public async Task SendAsync(
        string subject,
        string body,
        EmailNotificationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Email notifications are disabled.");
            return;
        }

        var recipients = options.Recipients
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (recipients.Length is 0)
        {
            logger.LogWarning("Email notification skipped because no recipients are configured.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.From))
        {
            logger.LogWarning("Email notification skipped because HealthNotifications:Email:From is empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Smtp.Host))
        {
            logger.LogWarning("Email notification skipped because HealthNotifications:Email:Smtp:Host is empty.");
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(options.From),
            Subject = subject,
            Body = body
        };

        foreach (var recipient in recipients)
        {
            message.To.Add(recipient);
        }

        using var client = new SmtpClient(options.Smtp.Host, options.Smtp.Port)
        {
            EnableSsl = options.Smtp.EnableSsl
        };

        if (!string.IsNullOrWhiteSpace(options.Smtp.UserName))
        {
            client.Credentials = new NetworkCredential(options.Smtp.UserName, options.Smtp.Password);
        }

        await client.SendMailAsync(message, cancellationToken);

        logger.LogInformation(
            "Email notification sent to {RecipientCount} recipients. Subject={Subject}",
            recipients.Length,
            subject);
    }
}
