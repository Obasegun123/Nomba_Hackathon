using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Nomba_Hackathon.Service;

public class EmailOptions
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
}

public class EmailService : IEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailOptions> options, ILogger<EmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string body, byte[]? attachmentBytes = null, string? attachmentFileName = null)
    {
        if (string.IsNullOrEmpty(_options.SmtpHost))
        {
            _logger.LogWarning("Email service not configured (SmtpHost is empty). Skipping send to {To}", to);
            return;
        }

        try
        {
            using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword)
            };

            using var message = new MailMessage
            {
                From = new MailAddress(_options.FromEmail ?? "noreply@nomba.local", _options.FromName ?? "Nomba"),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };

            message.To.Add(to);

            if (attachmentBytes != null && !string.IsNullOrEmpty(attachmentFileName))
            {
                using var stream = new MemoryStream(attachmentBytes);
                message.Attachments.Add(new Attachment(stream, attachmentFileName, "application/pdf"));
                await client.SendMailAsync(message);
            }
            else
            {
                await client.SendMailAsync(message);
            }

            _logger.LogInformation("Email sent to {To} with subject '{Subject}'", to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}", to);
            throw;
        }
    }
}
