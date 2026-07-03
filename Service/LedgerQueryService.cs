using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;

namespace Nomba_Hackathon.Service;

// CQRS read side. All query-only operations live here so the write path
// (PaymentService) stays free of read concerns.
public class LedgerQueryService
{
    private readonly LedgerDbContext _db;

    public LedgerQueryService(LedgerDbContext db) => _db = db;

    public sealed record BalanceResult(
        string AccountId, decimal Balance, decimal TotalCredit, decimal TotalDebit);

    public sealed record TransactionItem(
        Guid Id, string? ReferenceCode, string Status,
        string? AccountId, decimal Amount, DateTimeOffset CreatedAt);

    public sealed record TransactionPage(
        int Total, int Page, int PageSize, IReadOnlyList<TransactionItem> Items);

    public sealed record MetricsResult(
        IReadOnlyDictionary<string, int> ByStatus,
        decimal TotalCredit, decimal TotalDebit, decimal LedgerBalance,
        long EntryCount, bool Balanced, int PendingCount,
        bool PostgresOk, bool RedisOk);

    public async Task<BalanceResult> GetBalanceAsync(string accountId)
    {
        var totals = await _db.LedgerEntries
            .Where(e => e.AccountId == accountId)
            .GroupBy(_ => 1)
            .Select(g => new { Credit = g.Sum(x => x.CreditAmount), Debit = g.Sum(x => x.DebitAmount) })
            .FirstOrDefaultAsync();

        var credit = totals?.Credit ?? 0m;
        var debit  = totals?.Debit  ?? 0m;
        return new BalanceResult(accountId, credit - debit, credit, debit);
    }

    public async Task<TransactionPage> GetTransactionsAsync(
        string? status, string? accountId, int page, int pageSize)
    {
        var query = _db.Transactions.AsQueryable();
        if (!string.IsNullOrEmpty(status))    query = query.Where(t => t.Status == status);
        if (!string.IsNullOrEmpty(accountId)) query = query.Where(t => t.Entries.Any(e => e.AccountId == accountId));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * Math.Max(pageSize, 1))
            .Take(Math.Clamp(pageSize, 1, 200))
            .Select(t => new TransactionItem(t.Id, t.ReferenceCode, t.Status, t.AccountId, t.Amount, t.CreatedAt))
            .ToListAsync();

        return new TransactionPage(total, page, pageSize, items);
    }

    public async Task<MetricsResult> GetMetricsAsync(
        StackExchange.Redis.IConnectionMultiplexer redis)
    {
        var byStatus = await _db.Transactions
            .GroupBy(t => t.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();

        var ledger = await _db.LedgerEntries
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Credit = g.Sum(x => x.CreditAmount),
                Debit  = g.Sum(x => x.DebitAmount),
                Count  = (long)g.Count()
            })
            .FirstOrDefaultAsync();

        var credit  = ledger?.Credit ?? 0m;
        var debit   = ledger?.Debit  ?? 0m;
        var pending = byStatus.FirstOrDefault(x => x.Key == "PENDING")?.Count ?? 0;

        var postgresOk = await _db.Database.CanConnectAsync();
        bool redisOk;
        try { await redis.GetDatabase().PingAsync(); redisOk = true; }
        catch { redisOk = false; }

        return new MetricsResult(
            ByStatus:     byStatus.ToDictionary(x => x.Key, x => x.Count),
            TotalCredit:  credit,
            TotalDebit:   debit,
            LedgerBalance: credit - debit,
            EntryCount:   ledger?.Count ?? 0,
            Balanced:     credit - debit == 0,
            PendingCount: pending,
            PostgresOk:   postgresOk,
            RedisOk:      redisOk);
    }
}
