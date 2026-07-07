using System.Text;
using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;

namespace Nomba_Hackathon.Service;

public class HtmlStatementExporter
{
    private readonly LedgerDbContext _db;
    private readonly LedgerQueryService _queries;
    private readonly ILogger<HtmlStatementExporter> _logger;

    public HtmlStatementExporter(
        LedgerDbContext db,
        LedgerQueryService queries,
        ILogger<HtmlStatementExporter> logger)
    {
        _db = db;
        _queries = queries;
        _logger = logger;
    }

    public async Task<string> GenerateCustomerStatementAsync(
        string customerId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null)
    {
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer is null)
            throw new KeyNotFoundException($"Customer {customerId} not found");

        var accounts = await _db.VirtualAccounts
            .Where(a => a.CustomerId == customerId)
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<title>Customer Statement</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
        sb.AppendLine("h1 { color: #333; border-bottom: 2px solid #0066cc; padding-bottom: 10px; }");
        sb.AppendLine("h2 { color: #0066cc; margin-top: 20px; }");
        sb.AppendLine(".header { background: #f5f5f5; padding: 15px; border-radius: 4px; margin-bottom: 20px; }");
        sb.AppendLine(".header div { margin: 5px 0; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }");
        sb.AppendLine("th { background: #0066cc; color: white; padding: 10px; text-align: left; }");
        sb.AppendLine("td { padding: 8px; border-bottom: 1px solid #ddd; }");
        sb.AppendLine("tr:nth-child(even) { background: #f9f9f9; }");
        sb.AppendLine(".balance { font-weight: bold; color: #00cc00; }");
        sb.AppendLine(".negative { color: #cc0000; }");
        sb.AppendLine(".footer { margin-top: 30px; padding-top: 20px; border-top: 1px solid #ddd; color: #666; font-size: 12px; }");
        sb.AppendLine("@media print { body { margin: 0; } }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<h1>Customer Statement</h1>");
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"<div><strong>Customer:</strong> {HtmlEscape(customer.Name)}</div>");
        sb.AppendLine($"<div><strong>Email:</strong> {HtmlEscape(customer.Email)}</div>");
        sb.AppendLine($"<div><strong>ID:</strong> {HtmlEscape(customer.Id)}</div>");
        sb.AppendLine($"<div><strong>Status:</strong> {HtmlEscape(customer.Status)}</div>");
        sb.AppendLine($"<div><strong>KYC Tier:</strong> {customer.KycTier}</div>");
        sb.AppendLine($"<div><strong>Generated:</strong> {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Virtual Accounts</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Account Ref</th><th>Name</th><th>Status</th><th>Balance</th><th>NUBAN</th><th>Bank</th></tr>");

        foreach (var account in accounts)
        {
            var balance = await _queries.GetBalanceAsync(account.AccountRef);
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{HtmlEscape(account.AccountRef)}</td>");
            sb.AppendLine($"<td>{HtmlEscape(account.AccountName)}</td>");
            sb.AppendLine($"<td>{HtmlEscape(account.Status)}</td>");
            sb.AppendLine($"<td class=\"balance\">₦{balance?.Balance ?? 0m:F2}</td>");
            sb.AppendLine($"<td>{HtmlEscape(account.Nuban ?? "—")}</td>");
            sb.AppendLine($"<td>{HtmlEscape(account.BankName ?? "—")}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Transactions</h2>");
        var transactions = await GetTransactionsAsync(null, accounts.Select(a => a.AccountRef).ToList(), fromDate, toDate);

        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Date</th><th>Reference</th><th>Account</th><th>Amount</th><th>Status</th><th>Payer</th><th>Match Confidence</th></tr>");

        foreach (var tx in transactions)
        {
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{tx.CreatedAt:yyyy-MM-dd HH:mm:ss}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.ReferenceCode ?? "—")}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.AccountId ?? "—")}</td>");
            sb.AppendLine($"<td>₦{tx.Amount:F2}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.Status)}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.PayerName ?? "—")}</td>");
            sb.AppendLine($"<td>{(tx.MatchConfidence?.ToString("P2") ?? "—")}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</table>");

        sb.AppendLine("<div class=\"footer\">");
        sb.AppendLine("<p>This statement is confidential and intended only for the recipient. Generated by NexusLedger API.</p>");
        sb.AppendLine("</div>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        _logger.LogInformation("Generated HTML statement for customer {Customer}", customerId);
        return sb.ToString();
    }

    public async Task<string> GenerateAccountStatementAsync(
        string accountRef,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null)
    {
        var account = await _db.VirtualAccounts.FindAsync(accountRef);
        if (account is null)
            throw new KeyNotFoundException($"Account {accountRef} not found");

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<title>Account Statement</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
        sb.AppendLine("h1 { color: #333; border-bottom: 2px solid #0066cc; padding-bottom: 10px; }");
        sb.AppendLine("h2 { color: #0066cc; margin-top: 20px; }");
        sb.AppendLine(".header { background: #f5f5f5; padding: 15px; border-radius: 4px; margin-bottom: 20px; }");
        sb.AppendLine(".header div { margin: 5px 0; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }");
        sb.AppendLine("th { background: #0066cc; color: white; padding: 10px; text-align: left; }");
        sb.AppendLine("td { padding: 8px; border-bottom: 1px solid #ddd; }");
        sb.AppendLine("tr:nth-child(even) { background: #f9f9f9; }");
        sb.AppendLine(".balance { font-weight: bold; color: #00cc00; font-size: 18px; }");
        sb.AppendLine(".footer { margin-top: 30px; padding-top: 20px; border-top: 1px solid #ddd; color: #666; font-size: 12px; }");
        sb.AppendLine("@media print { body { margin: 0; } }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<h1>Virtual Account Statement</h1>");
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"<div><strong>Account Reference:</strong> {HtmlEscape(accountRef)}</div>");
        sb.AppendLine($"<div><strong>Account Name:</strong> {HtmlEscape(account.AccountName)}</div>");
        sb.AppendLine($"<div><strong>Status:</strong> {HtmlEscape(account.Status)}</div>");
        sb.AppendLine($"<div><strong>KYC Tier:</strong> {account.KycTier}</div>");
        sb.AppendLine($"<div><strong>NUBAN:</strong> {HtmlEscape(account.Nuban ?? "Not provisioned")}</div>");
        sb.AppendLine($"<div><strong>Bank:</strong> {HtmlEscape(account.BankName ?? "—")}</div>");

        var balance = await _queries.GetBalanceAsync(accountRef);
        sb.AppendLine($"<div class=\"balance\"><strong>Current Balance:</strong> ₦{balance?.Balance ?? 0m:F2}</div>");
        sb.AppendLine($"<div><strong>Generated:</strong> {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Transactions</h2>");
        var transactions = await GetTransactionsAsync(null, new List<string> { accountRef }, fromDate, toDate);

        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Date</th><th>Reference</th><th>Amount</th><th>Status</th><th>Payer Name</th><th>Match Type</th><th>Confidence</th></tr>");

        foreach (var tx in transactions)
        {
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{tx.CreatedAt:yyyy-MM-dd HH:mm:ss}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.ReferenceCode ?? "—")}</td>");
            sb.AppendLine($"<td>₦{tx.Amount:F2}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.Status)}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.PayerName ?? "—")}</td>");
            sb.AppendLine($"<td>{HtmlEscape(tx.MatchType ?? "—")}</td>");
            sb.AppendLine($"<td>{(tx.MatchConfidence?.ToString("P2") ?? "—")}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</table>");

        sb.AppendLine("<div class=\"footer\">");
        sb.AppendLine("<p>This statement is confidential and intended only for the recipient. Generated by NexusLedger API.</p>");
        sb.AppendLine("</div>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        _logger.LogInformation("Generated HTML statement for account {Account}", accountRef);
        return sb.ToString();
    }

    private async Task<List<Models.TransactionRecord>> GetTransactionsAsync(
        string? status,
        List<string> accountIds,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate)
    {
        var query = _db.Transactions
            .Where(t => accountIds.Contains(t.AccountId ?? ""))
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(t => t.Status == status);

        if (fromDate.HasValue)
            query = query.Where(t => t.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(t => t.CreatedAt <= toDate.Value);

        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
    }

    private static string HtmlEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }
}
