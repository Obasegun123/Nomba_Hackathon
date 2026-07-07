using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;
using Nomba_Hackathon.Models;

namespace Nomba_Hackathon.Service;

/// <summary>
/// Background reconciliation worker. Run on a Hangfire recurring schedule, it
/// polls Nomba for every locally-PENDING transaction and converges the local
/// ledger status with the authoritative upstream status. Exceptions are logged
/// to the database and optionally analyzed by an LLM for root cause diagnosis.
/// </summary>
public class ReconciliationService
{
    private readonly LedgerDbContext _db;
    private readonly IPaymentProvider _provider;
    private readonly PaymentService _payments;
    private readonly LedgerQueryService _ledgerQueries;
    private readonly VirtualAccountService _virtualAccounts;
    private readonly ILlmProvider? _llm;
    private readonly ISlackNotificationService? _slack;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        LedgerDbContext db,
        IPaymentProvider provider,
        PaymentService payments,
        LedgerQueryService ledgerQueries,
        VirtualAccountService virtualAccounts,
        ILogger<ReconciliationService> logger,
        ILlmProvider? llm = null,
        ISlackNotificationService? slack = null)
    {
        _db = db;
        _provider = provider;
        _payments = payments;
        _ledgerQueries = ledgerQueries;
        _virtualAccounts = virtualAccounts;
        _logger = logger;
        _llm = llm;
        _slack = slack;
    }

    /// <summary>
    /// Entry point for the Hangfire recurring job. For every locally-PENDING
    /// transaction we ask Nomba for the authoritative status and converge:
    ///   * SUCCESS -> post the balanced ledger entries via SettleAsync (idempotent)
    ///   * FAILED  -> mark the header FAILED (no entries)
    ///   * Ambiguous/error -> log exception, optionally trigger LLM analysis
    /// </summary>
    public async Task ReconcilePendingAsync()
    {
        var pending = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Status == "PENDING" && t.ReferenceCode != null)
            .OrderBy(t => t.CreatedAt)
            .Take(100)
            .ToListAsync();

        if (pending.Count == 0)
        {
            _logger.LogDebug("Reconciliation: no pending transactions");
            return;
        }

        _logger.LogInformation("Reconciliation: checking {Count} pending transactions", pending.Count);

        foreach (var t in pending)
        {
            try
            {
                var upstream = await _provider.GetTransactionAsync(t.ReferenceCode!);
                if (string.IsNullOrEmpty(upstream.Status))
                {
                    continue; // Could not resolve yet; leave PENDING for the next run.
                }

                var normalized = upstream.Status.ToUpperInvariant();
                if (normalized == "SUCCESS")
                {
                    await HandleSuccessAsync(t, upstream);
                }
                else if (normalized == "FAILED")
                {
                    await HandleFailedAsync(t);
                }
                else
                {
                    // Unexpected status code
                    await LogExceptionAsync(t, "unexpected_status",
                        $"Upstream returned unexpected status: {normalized}");
                }
            }
            catch (HttpRequestException ex)
            {
                // API connectivity issue
                _logger.LogWarning(ex, "Reconciliation API error for {Reference}", t.ReferenceCode);
                await LogExceptionAsync(t, "api_error", $"Failed to query upstream: {ex.Message}");
            }
            catch (Exception ex)
            {
                // One bad transaction must not abort the whole sweep.
                _logger.LogError(ex, "Reconciliation failed for {Reference}", t.ReferenceCode);
                await LogExceptionAsync(t, "unknown_error", $"Unexpected error: {ex.Message}");
            }
        }

        await _virtualAccounts.DetectMisdirectedPaymentsAsync();
        await _virtualAccounts.CheckKycLimitsAsync();
    }

    private async Task HandleSuccessAsync(TransactionRecord tx, ProviderTransactionStatus upstream)
    {
        // Check for amount mismatch
        if (upstream.Amount != null && upstream.Amount != tx.Amount)
        {
            _logger.LogWarning(
                "Amount mismatch for {Reference}: local={LocalAmount}, upstream={UpstreamAmount}",
                tx.ReferenceCode, tx.Amount, upstream.Amount);

            await LogExceptionAsync(tx, "amount_mismatch",
                $"Local: ₦{tx.Amount}, Upstream: ₦{upstream.Amount}");
            return;
        }

        // Check if ledger already has entries (double-settlement)
        var existingEntries = await _db.LedgerEntries
            .Where(e => e.TransactionId == tx.Id)
            .CountAsync();

        if (existingEntries > 0)
        {
            _logger.LogWarning("Double-settlement risk for {Reference}: {Count} entries already posted",
                tx.ReferenceCode, existingEntries);

            await LogExceptionAsync(tx, "double_settlement",
                $"Ledger already has {existingEntries} entries for this transaction");
            return;
        }

        // Normal path: settle with upstream amount
        var amount = upstream.Amount ?? tx.Amount;
        await _payments.SettleAsync(tx.ReferenceCode!, amount, tx.AccountId ?? "UNKNOWN");
        _logger.LogInformation("Reconciled {Reference} -> SUCCESS", tx.ReferenceCode);
    }

    private async Task HandleFailedAsync(TransactionRecord tx)
    {
        await _db.Transactions
            .Where(x => x.ReferenceCode == tx.ReferenceCode)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "FAILED"));
        _logger.LogInformation("Reconciled {Reference} -> FAILED", tx.ReferenceCode);
    }

    /// <summary>
    /// Log an exception, trigger LLM analysis if configured, and send Slack notification.
    /// </summary>
    private async Task LogExceptionAsync(TransactionRecord tx, string exceptionType, string errorMessage)
    {
        var exception = new ReconciliationException
        {
            Id = Guid.NewGuid(),
            TransactionRef = tx.ReferenceCode ?? "UNKNOWN",
            ExceptionType = exceptionType,
            ErrorMessage = errorMessage,
            Status = "PENDING",
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Optionally analyze with LLM
        if (_llm != null)
        {
            try
            {
                var context = await BuildExceptionContextAsync(tx, exceptionType, errorMessage);
                exception.ContextData = JsonSerializer.Serialize(context);

                var analysis = await _llm.AnalyzeExceptionAsync(context);
                exception.AiDiagnosis = analysis.Diagnosis;
                exception.AiRecommendation = analysis.Recommendation;
                exception.AiConfidence = analysis.Confidence;

                _logger.LogInformation(
                    "LLM analysis complete for {Reference} (confidence={Confidence}): {Diagnosis}",
                    tx.ReferenceCode, analysis.Confidence, analysis.Diagnosis);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM analysis failed for {Reference}, recording exception without diagnosis",
                    tx.ReferenceCode);
            }
        }

        _db.ReconciliationExceptions.Add(exception);
        await _db.SaveChangesAsync();

        // Send Slack notification (if configured)
        if (_slack != null)
        {
            try
            {
                await _slack.NotifyExceptionDetectedAsync(
                    exception.TransactionRef,
                    exception.ExceptionType,
                    exception.ErrorMessage,
                    exception.AiDiagnosis,
                    exception.AiRecommendation,
                    exception.AiConfidence);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Slack notification for {Reference}", tx.ReferenceCode);
            }
        }
    }

    private async Task<ExceptionContext> BuildExceptionContextAsync(
        TransactionRecord tx, string exceptionType, string errorMessage)
    {
        // Fetch ledger state
        var ledgerEntries = await _db.LedgerEntries
            .Where(e => e.TransactionId == tx.Id)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        var lastLedgerUpdate = ledgerEntries.FirstOrDefault()?.CreatedAt;
        var currentBalance = ledgerEntries.Count > 0
            ? ledgerEntries.Sum(e => e.CreditAmount - e.DebitAmount)
            : 0;

        // Try to get upstream state
        ProviderTransactionStatus? upstream = null;
        string? upstreamError = null;

        try
        {
            upstream = await _provider.GetTransactionAsync(tx.ReferenceCode!);
        }
        catch (Exception ex)
        {
            upstreamError = ex.Message;
        }

        return new ExceptionContext
        {
            TransactionRef = tx.ReferenceCode ?? "UNKNOWN",
            ExceptionType = exceptionType,
            ErrorMessage = errorMessage,
            Amount = tx.Amount,
            AccountId = tx.AccountId,
            CreatedAt = tx.CreatedAt,
            Status = tx.Status,
            UpstreamStatus = upstream?.Status,
            UpstreamAmount = upstream?.Amount,
            UpstreamError = upstreamError,
            CurrentBalance = currentBalance,
            LedgerEntryCount = ledgerEntries.Count,
            LastLedgerUpdate = lastLedgerUpdate
        };
    }
}
