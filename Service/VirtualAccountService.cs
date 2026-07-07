using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;
using Nomba_Hackathon.Models;

namespace Nomba_Hackathon.Service;

public class VirtualAccountService
{
    private readonly LedgerDbContext _db;
    private readonly NombaClient _nomba;
    private readonly ILogger<VirtualAccountService> _logger;
    private readonly WebhookEventPublisher _webhooks;

    public VirtualAccountService(
        LedgerDbContext db,
        NombaClient nomba,
        ILogger<VirtualAccountService> logger,
        WebhookEventPublisher webhooks)
    {
        _db = db;
        _nomba = nomba;
        _logger = logger;
        _webhooks = webhooks;
    }

    public async Task<VirtualAccount> ProvisionAsync(string customerId, string accountName, decimal amount = 0)
    {
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer is null)
            throw new InvalidOperationException($"Customer {customerId} not found");

        if (customer.Status != "ACTIVE")
            throw new InvalidOperationException($"Customer {customerId} is not active");

        var accountRef = GenerateAccountRef(customerId);

        var body = new Dictionary<string, object?>
        {
            ["accountRef"] = accountRef,
            ["accountName"] = accountName,
            ["currency"] = "NGN"
        };

        if (amount > 0)
            body["amount"] = amount;

        var nombaAccount = await _nomba.CreateVirtualAccountAsync(body);

        var nuban = nombaAccount?["bankAccountNumber"]?.GetValue<string>() ?? nombaAccount?["accountNumber"]?.GetValue<string>();
        var bankCode = nombaAccount?["bankCode"]?.GetValue<string>();
        var bankName = nombaAccount?["bankName"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(nuban) && !System.Text.RegularExpressions.Regex.IsMatch(nuban, @"^\d{10}$"))
        {
            _logger.LogWarning(
                "Nomba returned non-standard NUBAN {Nuban} for account {Ref}", nuban, accountRef);
        }

        if (!string.IsNullOrEmpty(nuban))
        {
            _logger.LogInformation(
                "Created virtual account {Ref} with NUBAN {Nuban} at {BankName}", accountRef, nuban, bankName);
        }

        var virtualAccount = new VirtualAccount
        {
            AccountRef = accountRef,
            CustomerId = customerId,
            AccountName = accountName,
            Status = "ACTIVE",
            KycTier = customer.KycTier,
            Nuban = nuban,
            BankCode = bankCode,
            BankName = bankName,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.VirtualAccounts.Add(virtualAccount);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Provisioned virtual account {AccountRef} for customer {CustomerId}",
            accountRef, customerId);

        await _webhooks.PublishAsync(
            "virtual_account.created",
            accountRef,
            customerId,
            new
            {
                eventType = "virtual_account.created",
                accountRef = accountRef,
                customerId = customerId,
                accountName = virtualAccount.AccountName,
                nuban = virtualAccount.Nuban,
                bankName = virtualAccount.BankName,
                bankCode = virtualAccount.BankCode,
                status = virtualAccount.Status,
                kycTier = virtualAccount.KycTier,
                createdAt = virtualAccount.CreatedAt
            });

        return virtualAccount;
    }

    public async Task<decimal> GetDailyTransactionAsync(string accountRef)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var total = await _db.LedgerEntries
            .Where(e => e.AccountId == accountRef && e.CreatedAt.Date == today)
            .SumAsync(e => e.CreditAmount);

        return total;
    }

    public async Task DetectMisdirectedPaymentsAsync()
    {
        var unsettledTransactions = await _db.Transactions
            .Where(t => t.Status == "SUCCESS")
            .Include(t => t.Entries)
            .ToListAsync();

        foreach (var tx in unsettledTransactions)
        {
            var accountId = tx.AccountId;
            if (string.IsNullOrEmpty(accountId))
                continue;

            var account = await _db.VirtualAccounts.FindAsync(accountId);
            if (account is null)
            {
                await LogMisdirectedPaymentAsync(
                    tx.ReferenceCode ?? "unknown",
                    accountId,
                    null,
                    tx.Amount,
                    "Virtual account does not exist");
            }
            else if (account.Status != "ACTIVE")
            {
                await LogMisdirectedPaymentAsync(
                    tx.ReferenceCode ?? "unknown",
                    accountId,
                    account.CustomerId,
                    tx.Amount,
                    $"Account is {account.Status.ToLower()}");
            }
        }
    }

    public async Task CheckKycLimitsAsync()
    {
        var accountsWithTransactions = await _db.LedgerEntries
            .GroupBy(e => e.AccountId)
            .Select(g => new { AccountId = g.Key, DailyTotal = g.Sum(e => e.CreditAmount) })
            .ToListAsync();

        foreach (var acct in accountsWithTransactions)
        {
            var virtualAccount = await _db.VirtualAccounts.FindAsync(acct.AccountId);
            if (virtualAccount is null || acct.DailyTotal == 0)
                continue;

            var limit = virtualAccount.KycTier switch
            {
                1 => 50000m,
                2 => 200000m,
                _ => decimal.MaxValue
            };

            if (acct.DailyTotal > limit)
            {
                _logger.LogWarning(
                    "KYC limit exceeded for account {AccountRef}: {DailyTotal} > {Limit}",
                    acct.AccountId, acct.DailyTotal, limit);

                var exception = new ReconciliationException
                {
                    Id = Guid.NewGuid(),
                    TransactionRef = acct.AccountId,
                    ExceptionType = "kyc_limit_exceeded",
                    ErrorMessage = $"Daily transaction limit of ₦{limit} exceeded: ₦{acct.DailyTotal}",
                    Status = "PENDING",
                    CreatedAt = DateTimeOffset.UtcNow
                };

                _db.ReconciliationExceptions.Add(exception);
            }
        }

        await _db.SaveChangesAsync();
    }

    public async Task RenameAccountAsync(string accountRef, string newName)
    {
        var account = await _db.VirtualAccounts.FindAsync(accountRef);
        if (account is null)
            throw new KeyNotFoundException($"Account {accountRef} not found");

        account.AccountName = newName;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Renamed account {AccountRef} to '{NewName}'", accountRef, newName);
    }

    public async Task ChangeAccountStatusAsync(string accountRef, string newStatus)
    {
        var account = await _db.VirtualAccounts.FindAsync(accountRef);
        if (account is null)
            throw new KeyNotFoundException($"Account {accountRef} not found");

        if (newStatus is not ("ACTIVE" or "CLOSED" or "SUSPENDED"))
            throw new ArgumentException("Invalid status. Must be ACTIVE, CLOSED, or SUSPENDED.");

        account.Status = newStatus;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Changed account {AccountRef} status to {Status}", accountRef, newStatus);
    }

    private async Task LogMisdirectedPaymentAsync(
        string transactionRef,
        string receivedAccountRef,
        string? intendedCustomerId,
        decimal amount,
        string reason)
    {
        var existing = await _db.MisdirectedPayments
            .FirstOrDefaultAsync(m => m.TransactionRef == transactionRef);

        if (existing is not null)
            return;

        var misdirected = new MisdirectedPayment
        {
            Id = Guid.NewGuid(),
            TransactionRef = transactionRef,
            ReceivedAccountRef = receivedAccountRef,
            IntendedCustomerId = intendedCustomerId,
            Amount = amount,
            Reason = reason,
            Status = "PENDING",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.MisdirectedPayments.Add(misdirected);
        await _db.SaveChangesAsync();

        _logger.LogWarning(
            "Misdirected payment {Ref} detected: {Amount}₦ to {ReceivedAccount}. Reason: {Reason}",
            transactionRef, amount, receivedAccountRef, reason);
    }

    private static string GenerateAccountRef(string customerId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{customerId}_{timestamp}";
    }
}
