using System.Text;
using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;

namespace Nomba_Hackathon.Service;

public class CsvStatementExporter
{
    private readonly LedgerDbContext _db;
    private readonly LedgerQueryService _queries;
    private readonly ILogger<CsvStatementExporter> _logger;

    public CsvStatementExporter(
        LedgerDbContext db,
        LedgerQueryService queries,
        ILogger<CsvStatementExporter> logger)
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
            .Select(a => a.AccountRef)
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("CUSTOMER STATEMENT");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine($"Customer ID,{EscapeCsv(customer.Id)}");
        sb.AppendLine($"Customer Name,{EscapeCsv(customer.Name)}");
        sb.AppendLine($"Email,{EscapeCsv(customer.Email)}");
        sb.AppendLine($"Status,{EscapeCsv(customer.Status)}");
        sb.AppendLine($"KYC Tier,{customer.KycTier}");
        sb.AppendLine();

        sb.AppendLine("ACCOUNTS");
        sb.AppendLine("AccountRef,AccountName,Status,Balance,NUBAN,BankName");

        foreach (var accountRef in accounts)
        {
            var account = await _db.VirtualAccounts.FindAsync(accountRef);
            var balance = await _queries.GetBalanceAsync(accountRef);

            sb.AppendLine($"{EscapeCsv(accountRef)},{EscapeCsv(account?.AccountName ?? "")},{EscapeCsv(account?.Status ?? "")},{balance?.Balance ?? 0m},{EscapeCsv(account?.Nuban ?? "")},{EscapeCsv(account?.BankName ?? "")}");
        }

        sb.AppendLine();
        sb.AppendLine("TRANSACTIONS");
        sb.AppendLine("Date,Reference,Account,Payer,Amount,Status,MatchType,MatchConfidence");

        var transactions = await GetTransactionsAsync(null, accounts, fromDate, toDate);

        foreach (var tx in transactions)
        {
            sb.AppendLine($"{tx.CreatedAt:yyyy-MM-dd HH:mm:ss},{EscapeCsv(tx.ReferenceCode ?? "")},{EscapeCsv(tx.AccountId ?? "")},{EscapeCsv(tx.PayerName ?? "")},{tx.Amount},{EscapeCsv(tx.Status)},{EscapeCsv(tx.MatchType ?? "")},{tx.MatchConfidence?.ToString("P4") ?? ""}");
        }

        _logger.LogInformation(
            "Generated CSV statement for customer {CustomerId}: {Lines} lines",
            customerId, sb.ToString().Split('\n').Length);

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
        sb.AppendLine("VIRTUAL ACCOUNT STATEMENT");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine($"Account Reference,{EscapeCsv(accountRef)}");
        sb.AppendLine($"Account Name,{EscapeCsv(account.AccountName)}");
        sb.AppendLine($"Status,{EscapeCsv(account.Status)}");
        sb.AppendLine($"KYC Tier,{account.KycTier}");
        sb.AppendLine($"NUBAN,{EscapeCsv(account.Nuban ?? "")}");
        sb.AppendLine($"Bank,{EscapeCsv(account.BankName ?? "")}");

        var balance = await _queries.GetBalanceAsync(accountRef);
        sb.AppendLine($"Current Balance,{balance?.Balance ?? 0m}");
        sb.AppendLine();

        sb.AppendLine("TRANSACTIONS");
        sb.AppendLine("Date,Reference,Amount,Status,Payer,MatchType,MatchConfidence");

        var transactions = await GetTransactionsAsync(null, new[] { accountRef }, fromDate, toDate);

        foreach (var tx in transactions)
        {
            sb.AppendLine($"{tx.CreatedAt:yyyy-MM-dd HH:mm:ss},{EscapeCsv(tx.ReferenceCode ?? "")},{tx.Amount},{EscapeCsv(tx.Status)},{EscapeCsv(tx.PayerName ?? "")},{EscapeCsv(tx.MatchType ?? "")},{tx.MatchConfidence?.ToString("P4") ?? ""}");
        }

        _logger.LogInformation(
            "Generated CSV statement for account {Account}: {Lines} lines",
            accountRef, sb.ToString().Split('\n').Length);

        return sb.ToString();
    }

    private async Task<List<Models.TransactionRecord>> GetTransactionsAsync(
        string? status,
        IEnumerable<string> accountIds,
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

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";

        return value;
    }
}
