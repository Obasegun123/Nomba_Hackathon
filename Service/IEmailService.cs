namespace Nomba_Hackathon.Service;

public interface IEmailService
{
    Task SendAsync(string to, string subject, string body, byte[]? attachmentBytes = null, string? attachmentFileName = null);
}
