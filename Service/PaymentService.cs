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
    private readonly FuzzyMatchingService _fuzzyMatching;
    private readonly WebhookEventPublisher _webhooks;

    public PaymentService(
        LedgerDbContext db,
        ILogger<PaymentService> logger,
        FuzzyMatchingService fuzzyMatching,
        WebhookEventPublisher webhooks)
    {
        _db = db;
        _logger = logger;
        _fuzzyMatching = fuzzyMatching;
        _webhooks = webhooks;
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

        await SettleAsync(parsed.Reference, parsed.Amount, parsed.AccountId, parsed.Expected, parsed.PayerName);
    }

    public async Task ProcessReversalAsync(string rawBody)
    {
        var parsed = ParsePayload(rawBody);
        if (parsed is null)
        {
            _logger.LogWarning("Could not parse reversal payload; skipping reversal processing");
            return;
        }

        await ReverseAsync(parsed.Reference, parsed.Amount, parsed.AccountId);
    }

    public async Task ReverseAsync(string originalReference, decimal reversalAmount, string accountId)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => ReverseCoreAsync(originalReference, reversalAmount, accountId));
    }

    private async Task ReverseCoreAsync(string originalReference, decimal reversalAmount, string accountId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var originalTransaction = await _db.Transactions
                .Include(t => t.Entries)
                .FirstOrDefaultAsync(t => t.ReferenceCode == originalReference);

            if (originalTransaction is null)
            {
                _logger.LogWarning("Reversal requested for unknown transaction {Reference}", originalReference);
                await tx.CommitAsync();
                return;
            }

            if (originalTransaction.Status == "REVERSED")
            {
                _logger.LogInformation("Transaction {Reference} already reversed; skipping", originalReference);
                await tx.CommitAsync();
                return;
            }

            var reversalRef = $"{originalReference}_REVERSAL_{Guid.NewGuid().ToString("N").Substring(0, 8)}";

            var reversalTransaction = new TransactionRecord
            {
                Id = Guid.NewGuid(),
                ReferenceCode = reversalRef,
                Status = "REVERSED",
                AccountId = accountId,
                Amount = reversalAmount,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Transactions.Add(reversalTransaction);

            var creditAccount = originalTransaction.AccountId ?? accountId;

            reversalTransaction.Entries.Add(new LedgerEntry
            {
                AccountId = creditAccount,
                DebitAmount = reversalAmount,
                EntryType = "REVERSAL",
                CreatedAt = DateTimeOffset.UtcNow
            });
            reversalTransaction.Entries.Add(new LedgerEntry
            {
                AccountId = SystemClearingAccount,
                CreditAmount = reversalAmount,
                EntryType = "REVERSAL",
                CreatedAt = DateTimeOffset.UtcNow
            });

            originalTransaction.Status = "REVERSED";

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogWarning(
                "Transaction {Original} reversed: {Amount} reversed via {Reversal}",
                originalReference, reversalAmount, reversalRef);

            var account = await _db.VirtualAccounts.FindAsync(creditAccount);
            var customer = account is not null
                ? await _db.Customers.FindAsync(account.CustomerId)
                : null;

            await _webhooks.PublishAsync(
                "virtual_account.reversal_detected",
                creditAccount,
                customer?.Id,
                new
                {
                    eventType = "virtual_account.reversal_detected",
                    accountRef = creditAccount,
                    originalTransaction = originalReference,
                    reversalTransaction = reversalRef,
                    reversalAmount = reversalAmount,
                    reversedAt = DateTimeOffset.UtcNow
                });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Reversal processing failed for {Reference}; rolled back", originalReference);
            throw;
        }
    }

    // Idempotently books a balanced double-entry for a transaction:
    //   * unknown reference        -> create header + post entries
    //   * existing PENDING header  -> post entries and flip status (webhook/recon settle)
    //   * already has entries      -> true duplicate, skip
    // The over/under status is derived against the expected amount (the PENDING
    // header amount when present, otherwise the value carried in the payload).
    public async Task SettleAsync(string reference, decimal actualAmount, string accountId, decimal? expectedFromPayload = null, string? payerName = null)
    {
        // EnableRetryOnFailure (Program.cs) forbids a bare BeginTransactionAsync —
        // a retry mid-transaction would silently replay writes twice. Running the
        // whole unit of work through the execution strategy lets EF Core retry the
        // entire transaction atomically instead.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => SettleCoreAsync(reference, actualAmount, accountId, expectedFromPayload, payerName));
    }

    private async Task SettleCoreAsync(string reference, decimal actualAmount, string accountId, decimal? expectedFromPayload, string? payerName)
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

            // Fuzzy match payer name against account name
            string? matchType = null;
            decimal? matchConfidence = null;
            if (!string.IsNullOrEmpty(payerName) && virtualAccount is not null)
            {
                var matchResult = _fuzzyMatching.Match(payerName, virtualAccount.AccountName, 0.85f);
                if (matchResult.IsMatch)
                {
                    matchType = matchResult.Confidence == 1f ? "EXACT_MATCH" : "FUZZY_MATCHED";
                    matchConfidence = (decimal)matchResult.Confidence;
                    _logger.LogInformation(
                        "Name match for {Reference}: '{Incoming}' vs '{Expected}' -> {Type} ({Confidence:P0})",
                        reference, payerName, virtualAccount.AccountName, matchType, matchResult.Confidence);
                }
                else
                {
                    matchConfidence = (decimal)matchResult.Confidence;
                    _logger.LogWarning(
                        "Name mismatch for {Reference}: '{Incoming}' vs '{Expected}' ({Confidence:P0}) - {Reason}",
                        reference, payerName, virtualAccount.AccountName, matchResult.Confidence, matchResult.Reason);
                }
            }

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
                transaction.PayerName = payerName;
                transaction.MatchType = matchType;
                transaction.MatchConfidence = matchConfidence;
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
                    transaction.PayerName = payerName;
                    transaction.MatchType = matchType;
                    transaction.MatchConfidence = matchConfidence;
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
                if (actualAmount > exp)
                {
                    status = "OVERPAYMENT";
                }
                else if (actualAmount > 0 && actualAmount < exp)
                {
                    status = "PARTIAL_PENDING";
                    _logger.LogInformation(
                        "Payment {Reference} is partial: received {Received}, expected {Expected}",
                        reference, actualAmount, exp);
                }
                else
                {
                    status = "UNDERPAYMENT";
                }

                if (status != "PARTIAL_PENDING")
                {
                    _logger.LogWarning(
                        "Payment {Reference} amount mismatch: received {Received}, expected {Expected} -> {Status}",
                        reference, actualAmount, exp, status);
                }
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
            transaction.PayerName = payerName;
            transaction.MatchType = matchType;
            transaction.MatchConfidence = matchConfidence;

            if (status == "PARTIAL_PENDING" && expected is decimal expectedAmount)
            {
                var plan = await _db.PaymentPlans
                    .FirstOrDefaultAsync(p => p.TransactionRef == reference);

                if (plan is null)
                {
                    plan = new PaymentPlan
                    {
                        Id = Guid.NewGuid(),
                        TransactionRef = reference,
                        AccountRef = creditAccount,
                        TotalExpected = expectedAmount,
                        TotalReceived = actualAmount,
                        RemainingBalance = expectedAmount - actualAmount,
                        Status = "PENDING",
                        InstallmentsReceived = 1,
                        CreatedAt = DateTimeOffset.UtcNow,
                        LastPaymentAt = DateTimeOffset.UtcNow
                    };
                    _db.PaymentPlans.Add(plan);
                }
                else
                {
                    plan.TotalReceived += actualAmount;
                    plan.RemainingBalance = plan.TotalExpected - plan.TotalReceived;
                    plan.InstallmentsReceived++;
                    plan.LastPaymentAt = DateTimeOffset.UtcNow;

                    if (plan.RemainingBalance <= 0)
                    {
                        plan.Status = "COMPLETED";
                        plan.CompletedAt = DateTimeOffset.UtcNow;
                        status = "SUCCESS";
                        _logger.LogInformation(
                            "Payment plan {PlanId} completed: received {Received}/{Expected}",
                            plan.Id, plan.TotalReceived, plan.TotalExpected);
                    }
                }

                transaction.PaymentPlanId = plan.Id;

                if (status == "PARTIAL_PENDING")
                {
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();

                    _logger.LogInformation(
                        "Partial payment {Reference}: received {Received}/{Expected}, {Remaining} remaining",
                        reference, plan.TotalReceived, plan.TotalExpected, plan.RemainingBalance);
                    return;
                }
            }
            else
            {
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

                var customer = virtualAccount is not null
                    ? await _db.Customers.FindAsync(virtualAccount.CustomerId)
                    : null;

                await _webhooks.PublishAsync(
                    "virtual_account.funded",
                    creditAccount,
                    customer?.Id,
                    new
                    {
                        eventType = "virtual_account.funded",
                        accountRef = creditAccount,
                        transactionReference = reference,
                        amount = actualAmount,
                        status = status,
                        matchType = matchType,
                        matchConfidence = matchConfidence,
                        payerName = payerName,
                        settledAt = DateTimeOffset.UtcNow
                    });
            }
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Ledger write failed for {Reference}; rolled back", reference);
            throw;
        }
    }

    private sealed record ParsedPayment(string Reference, decimal Amount, decimal? Expected, string AccountId, string? PayerName);

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

            // Payer name from transaction (usually from sender details in payment_success)
            var payerName =
                GetString(tx, "senderName") ??
                GetString(tx, "senderFullName") ??
                GetString(tx, "senderAccountName") ??
                GetString(data, "senderName");

            return new ParsedPayment(reference, amount, expected, accountId, payerName);
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
