using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;

namespace Nomba_Hackathon.Service;

// Background reconciliation worker. Run on a Hangfire recurring schedule, it
// polls Nomba for every locally-PENDING transaction and converges the local
// ledger status with the authoritative upstream status.
public class ReconciliationService
{
    private readonly LedgerDbContext _db;
    private readonly IPaymentProvider _provider;
    private readonly PaymentService _payments;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        LedgerDbContext db,
        IPaymentProvider provider,
        PaymentService payments,
        ILogger<ReconciliationService> logger)
    {
        _db = db;
        _provider = provider;
        _payments = payments;
        _logger = logger;
    }

    // Entry point for the Hangfire recurring job. For every locally-PENDING
    // transaction we ask Nomba for the authoritative status and converge:
    //   * SUCCESS -> post the balanced ledger entries via SettleAsync (idempotent)
    //   * FAILED  -> mark the header FAILED (no entries)
    // A self-describing PENDING header (amount + account captured at initiation)
    // lets us fully settle a payment whose webhook was lost.
    public async Task ReconcilePendingAsync()
    {
        var pending = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Status == "PENDING" && t.ReferenceCode != null)
            .OrderBy(t => t.CreatedAt)
            .Take(100)
            .Select(t => new { t.ReferenceCode, t.AccountId, t.Amount })
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
                    // Prefer the upstream amount; fall back to the expected amount
                    // captured on the header at initiation.
                    var amount = upstream.Amount ?? t.Amount;
                    await _payments.SettleAsync(t.ReferenceCode!, amount, t.AccountId ?? "UNKNOWN");
                    _logger.LogInformation("Reconciled {Reference} -> SUCCESS", t.ReferenceCode);
                }
                else if (normalized == "FAILED")
                {
                    await _db.Transactions
                        .Where(x => x.ReferenceCode == t.ReferenceCode)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "FAILED"));
                    _logger.LogInformation("Reconciled {Reference} -> FAILED", t.ReferenceCode);
                }
            }
            catch (Exception ex)
            {
                // One bad transaction must not abort the whole sweep.
                _logger.LogError(ex, "Reconciliation failed for {Reference}", t.ReferenceCode);
            }
        }
    }
}
