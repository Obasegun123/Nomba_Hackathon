using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;

namespace Nomba_Hackathon.Service;

public class MonthlyStatementService
{
    private readonly LedgerDbContext _db;
    private readonly PdfStatementGenerator _pdfGenerator;
    private readonly IEmailService _email;
    private readonly ILogger<MonthlyStatementService> _logger;

    public MonthlyStatementService(
        LedgerDbContext db,
        PdfStatementGenerator pdfGenerator,
        IEmailService email,
        ILogger<MonthlyStatementService> logger)
    {
        _db = db;
        _pdfGenerator = pdfGenerator;
        _email = email;
        _logger = logger;
    }

    public async Task SendMonthlyStatementsAsync()
    {
        _logger.LogInformation("Starting monthly statement batch send");

        var customers = await _db.Customers
            .Where(c => c.Status == "ACTIVE")
            .ToListAsync();

        if (customers.Count == 0)
        {
            _logger.LogInformation("No active customers found for statement send");
            return;
        }

        var sent = 0;
        var failed = 0;

        foreach (var customer in customers)
        {
            try
            {
                var pdfBytes = await _pdfGenerator.GenerateCustomerStatementAsync(customer.Id);
                var fileName = $"statement_{customer.Id}_{DateTimeOffset.UtcNow:yyyyMM}.pdf";
                var subject = $"Monthly Account Statement - {DateTimeOffset.UtcNow:MMMM yyyy}";
                var body = $@"
Dear {customer.Name},

Please find your monthly account statement for {DateTimeOffset.UtcNow:MMMM yyyy} attached.

If you have any questions about your transactions or account, please contact our support team.

Best regards,
Nomba Financial Services
";

                await _email.SendAsync(customer.Email, subject, body, pdfBytes, fileName);
                sent++;

                _logger.LogInformation("Monthly statement sent to {CustomerEmail} ({CustomerId})", customer.Email, customer.Id);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Failed to send monthly statement to customer {CustomerId} ({Email})", customer.Id, customer.Email);
            }
        }

        _logger.LogInformation("Monthly statement batch complete: {Sent} sent, {Failed} failed out of {Total}",
            sent, failed, customers.Count);
    }
}
