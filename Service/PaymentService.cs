using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;
using Nomba_Hackathon.Models;

namespace Nomba_Hackathon.Service;

// Owns the ledger write-path. Turns a verified Nomba webhook payload into a
// balanced double-entry transaction inside a single atomic DB transaction.
public class PaymentService
{
    public const string SystemClearingAccount = "SYSTEM_CLEARING";

    private readonly LedgerDbContext _db;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(LedgerDbContext db, ILogger<PaymentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Records a freshly-initiated payment as a PENDING header (no ledger entries
    // yet). The webhook — or, if it is lost, the reconciliation worker — later
    // settles it via SettleAsync. Returns false if the reference already exists.
    public async Task<bool> CreatePendingAsync(string reference, string accountId, decimal amount)
    {
        bool exists = await _db.Transactions.AnyAsync(t => t.ReferenceCode == reference);
        if (exists)
        {
            _logger.LogInformation("Pending {Reference} already exists; not re-creating", reference);
            return false;
        }

        _db.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            ReferenceCode = reference,
            Status = "PENDING",
            AccountId = accountId,
            Amount = amount,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Created PENDING {Reference} for {Amount} on account {Account}", reference, amount, accountId);
        return true;
    }

    // Invoked by the Hangfire worker off the webhook. Must be public and take
    // serialisable args. Parses the payload then delegates to SettleAsync.
    public async Task ProcessSuccessfulPayment(string rawBody)
    {
        var parsed = ParsePayload(rawBody);
        if (parsed is null)
        {
            _logger.LogWarning("Could not parse Nomba payload; skipping ledger write");
            return;
        }

        await SettleAsync(parsed.Reference, parsed.Amount, parsed.AccountId, parsed.Expected);
    }

    // Idempotently books a balanced double-entry for a transaction:
    //   * unknown reference        -> create header + post entries
    //   * existing PENDING header  -> post entries and flip status (webhook/recon settle)
    //   * already has entries      -> true duplicate, skip
    // The over/under status is derived against the expected amount (the PENDING
    // header amount when present, otherwise the value carried in the payload).
    public async Task SettleAsync(string reference, decimal actualAmount, string accountId, decimal? expectedFromPayload = null)
    {
        // EnableRetryOnFailure (Program.cs) forbids a bare BeginTransactionAsync —
        // a retry mid-transaction would silently replay writes twice. Running the
        // whole unit of work through the execution strategy lets EF Core retry the
        // entire transaction atomically instead.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => SettleCoreAsync(reference, actualAmount, accountId, expectedFromPayload));
    }

    private async Task SettleCoreAsync(string reference, decimal actualAmount, string accountId, decimal? expectedFromPayload)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var transaction = await _db.Transactions
                .Include(t => t.Entries)
                .FirstOrDefaultAsync(t => t.ReferenceCode == reference);

            if (transaction is not null && transaction.Entries.Count > 0)
            {
                _logger.LogInformation(
                    "Transaction {Reference} already settled; skipping", reference);
                await tx.CommitAsync();
                return;
            }

            // Prefer the account/expected captured at initiation when available.
            var creditAccount = !string.IsNullOrEmpty(transaction?.AccountId)
                ? transaction!.AccountId!
                : accountId;
            var expected = transaction is { Amount: > 0 } ? transaction.Amount : expectedFromPayload;

            // --- Account lifecycle guards (only enforced when the account is registered) ---
            var virtualAccount = await _db.VirtualAccounts
                .FirstOrDefaultAsync(a => a.AccountRef == creditAccount);

            if (virtualAccount is { Status: "CLOSED" or "SUSPENDED" })
            {
                // Reroute to MISDIRECTED — preserves double-entry; original account balance stays zero.
                if (transaction is null)
                {
                    transaction = new TransactionRecord
                    {
                        Id = Guid.NewGuid(), ReferenceCode = reference, CreatedAt = DateTimeOffset.UtcNow
                    };
                    _db.Transactions.Add(transaction);
                }
                transaction.Status = "MISDIRECTED";
                transaction.AccountId = creditAccount;
                transaction.Amount = actualAmount;
                transaction.Entries.Add(new LedgerEntry
                {
                    AccountId = "MISDIRECTED", CreditAmount = actualAmount,
                    EntryType = "PAYMENT", CreatedAt = DateTimeOffset.UtcNow
                });
                transaction.Entries.Add(new LedgerEntry
                {
                    AccountId = SystemClearingAccount, DebitAmount = actualAmount,
                    EntryType = "PAYMENT", CreatedAt = DateTimeOffset.UtcNow
                });
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                _logger.LogWarning(
                    "Payment {Reference} ({Amount}) rerouted to MISDIRECTED: account {Account} is {Status}",
                    reference, actualAmount, creditAccount, virtualAccount.Status);
                return;
            }

            if (virtualAccount is { KycTier: < 3 })
            {
                decimal dailyLimit = virtualAccount.KycTier == 1 ? 50_000m : 200_000m;
                var dayStart = DateTimeOffset.UtcNow.Date;
                var todayCredit = await _db.LedgerEntries
                    .Where(e => e.AccountId == creditAccount
                                && e.CreditAmount > 0
                                && e.CreatedAt >= dayStart)
                    .SumAsync(e => e.CreditAmount);

                if (todayCredit + actualAmount > dailyLimit)
                {
                    if (transaction is null)
                    {
                        transaction = new TransactionRecord
                        {
                            Id = Guid.NewGuid(), ReferenceCode = reference, CreatedAt = DateTimeOffset.UtcNow
                        };
                        _db.Transactions.Add(transaction);
                    }
                    transaction.Status = "KYC_LIMIT_EXCEEDED";
                    transaction.AccountId = creditAccount;
                    transaction.Amount = actualAmount;
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();
                    _logger.LogWarning(
                        "Payment {Reference} ({Amount} NGN) exceeds KYC tier {Tier} daily limit ({Limit} NGN) for {Account}; today credited {Today} NGN",
                        reference, actualAmount, virtualAccount.KycTier, dailyLimit, creditAccount, todayCredit);
                    return;
                }
            }
            // ---------------------------------------------------------------------------

            var status = "SUCCESS";
            if (expected is decimal exp && exp != actualAmount)
            {
                status = actualAmount > exp ? "OVERPAYMENT" : "UNDERPAYMENT";
                _logger.LogWarning(
                    "Payment {Reference} amount mismatch: received {Received}, expected {Expected} -> {Status}",
                    reference, actualAmount, exp, status);
            }

            if (transaction is null)
            {
                transaction = new TransactionRecord
                {
                    Id = Guid.NewGuid(),
                    ReferenceCode = reference,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                _db.Transactions.Add(transaction);
            }

            transaction.Status = status;
            transaction.AccountId = creditAccount;
            transaction.Amount = actualAmount;

            // Double-entry: credit the customer account, debit the system clearing
            // account so total debits == total credits.
            transaction.Entries.Add(new LedgerEntry
            {
                AccountId = creditAccount,
                CreditAmount = actualAmount,
                EntryType = "PAYMENT",
                CreatedAt = DateTimeOffset.UtcNow
            });
            transaction.Entries.Add(new LedgerEntry
            {
                AccountId = SystemClearingAccount,
                DebitAmount = actualAmount,
                EntryType = "PAYMENT",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation(
                "Ledger settled for {Reference} ({Amount}) on account {Account}",
                reference, actualAmount, creditAccount);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Ledger write failed for {Reference}; rolled back", reference);
            throw;
        }
    }

    private sealed record ParsedPayment(string Reference, decimal Amount, decimal? Expected, string AccountId);

    // Handles both Nomba payload shapes:
    //  payment_success (real event): data.merchant + data.transaction nested objects
    //  checkout/legacy:              data.order.* / data.transaction.*
    private static ParsedPayment? ParsePayload(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;

            // payment_success shape: data.transaction holds tx details
            data.TryGetProperty("transaction", out var tx);
            data.TryGetProperty("merchant",    out var merchant);
            // checkout/legacy shape: data.order
            data.TryGetProperty("order",       out var order);

            // Reference: virtual account alias ref > transactionId > orderReference > requestId
            var reference =
                GetString(tx, "aliasAccountReference") ??
                GetString(tx, "transactionId") ??
                GetString(order, "orderReference") ??
                GetString(root, "requestId");

            if (reference is null) return null;

            var amount =
                GetDecimal(tx,    "transactionAmount") ??
                GetDecimal(order, "amount") ??
                0m;

            // No amountExpected in payment_success; checkout carries it in order.amount
            var expected = GetDecimal(order, "amount");

            // Account: the virtual account alias reference is the stable identifier
            var accountId =
                GetString(tx,      "aliasAccountReference") ??
                GetString(merchant, "walletId") ??
                GetString(order,   "accountId") ??
                "UNKNOWN";

            return new ParsedPayment(reference, amount, expected, accountId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static decimal? GetDecimal(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal()
            : null;
}
