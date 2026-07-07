using System.Text;
using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;
using Nomba_Hackathon.Models;

namespace Nomba_Hackathon.Service;

public class DemoService
{
    private readonly LedgerDbContext _db;
    private readonly PaymentService _payments;
    private readonly ILogger<DemoService> _logger;

    public DemoService(LedgerDbContext db, PaymentService payments, ILogger<DemoService> logger)
    {
        _db = db;
        _payments = payments;
        _logger = logger;
    }

    public async Task<SettlementAccuracyReport> GenerateSettlementAccuracyReportAsync(int? seed = null)
    {
        var startTime = DateTimeOffset.UtcNow;
        var timestamp = startTime.ToString("yyyyMMddHHmmss");

        using var transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            var rand = seed.HasValue ? new Random(seed.Value) : new Random();
            var testTransactions = GenerateTestTransactions(20, timestamp, rand);

            _logger.LogInformation("Demo: Creating {Count} test transactions", testTransactions.Count);

            var createdTxIds = new List<Guid>();
            var references = new List<string>();

            foreach (var testTx in testTransactions)
            {
                await _payments.CreatePendingAsync(
                    testTx.ReferenceCode,
                    testTx.AccountId ?? "DEMO_ACC_1",
                    testTx.Amount);

                var tx = await _db.Transactions
                    .FirstOrDefaultAsync(t => t.ReferenceCode == testTx.ReferenceCode);

                if (tx != null)
                {
                    createdTxIds.Add(tx.Id);
                    references.Add(testTx.ReferenceCode);

                    if (testTx.Status == "SUCCESS")
                    {
                        await _payments.SettleAsync(
                            testTx.ReferenceCode,
                            testTx.Amount,
                            testTx.AccountId ?? "DEMO_ACC_1");
                    }
                    else if (testTx.Status == "FAILED")
                    {
                        await _db.Transactions
                            .Where(t => t.ReferenceCode == testTx.ReferenceCode)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "FAILED"));
                    }
                }
            }

            _logger.LogInformation("Demo: Settled {Count} test transactions", createdTxIds.Count);

            var ledgerEntries = await _db.LedgerEntries
                .Where(e => createdTxIds.Contains(e.TransactionId))
                .ToListAsync();

            var totalCredit = ledgerEntries.Sum(e => e.CreditAmount);
            var totalDebit = ledgerEntries.Sum(e => e.DebitAmount);
            var isBalanced = totalCredit == totalDebit;

            var settlements = testTransactions
                .Where(t => t.Status == "SUCCESS" || t.Status == "PENDING")
                .ToList();

            var settlementCsv = GenerateSettlementCsv(settlements);

            var matchedCount = 0;
            var mismatchedCount = 0;

            foreach (var ref_code in references)
            {
                var tx = await _db.Transactions
                    .FirstOrDefaultAsync(t => t.ReferenceCode == ref_code);

                if (tx != null && settlements.Any(s => s.ReferenceCode == ref_code))
                {
                    var settlement = settlements.First(s => s.ReferenceCode == ref_code);
                    if (tx.Amount == settlement.Amount &&
                        (tx.Status == "SUCCESS" || settlement.Status == "SUCCESS"))
                    {
                        matchedCount++;
                    }
                    else
                    {
                        mismatchedCount++;
                    }
                }
            }

            await transaction.CommitAsync();

            var report = new SettlementAccuracyReport
            {
                TestRunId = Guid.NewGuid().ToString(),
                Timestamp = startTime,
                TotalTransactionsCreated = testTransactions.Count,
                SuccessfulTransactions = testTransactions.Count(t => t.Status == "SUCCESS"),
                PendingTransactions = testTransactions.Count(t => t.Status == "PENDING"),
                FailedTransactions = testTransactions.Count(t => t.Status == "FAILED"),
                LedgerEntriesPosted = ledgerEntries.Count,
                TotalCredit = totalCredit,
                TotalDebit = totalDebit,
                LedgerBalanced = isBalanced,
                SettlementFileGenerated = settlementCsv,
                Matched = matchedCount,
                Mismatched = mismatchedCount,
                Unknown = 0,
                MatchRate = settlements.Count > 0 ? Math.Round((double)matchedCount / settlements.Count * 100, 1) : 0,
                SampleTransactions = testTransactions.Take(5)
                    .Select(t => (object)new
                    {
                        ReferenceCode = t.Item1,
                        Amount = t.Item3,
                        Status = t.Item4,
                        AccountId = t.Item2
                    })
                    .ToList()
            };

            _logger.LogInformation(
                "Demo settlement accuracy: {Matched}/{Total} matched ({MatchRate}%), ledger balanced: {Balanced}",
                report.Matched, settlements.Count, report.MatchRate, report.LedgerBalanced);

            return report;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Demo settlement accuracy test failed");
            throw;
        }
    }

    private List<(string ReferenceCode, string AccountId, decimal Amount, string Status)> GenerateTestTransactions(
        int count,
        string timestamp,
        Random rand)
    {
        var transactions = new List<(string, string, decimal, string)>();
        var statuses = new[] { "SUCCESS", "SUCCESS", "SUCCESS", "SUCCESS", "SUCCESS", "PENDING", "PENDING", "PENDING", "FAILED" };

        for (int i = 0; i < count; i++)
        {
            var refCode = $"DEMO_SETTLE_{timestamp}_{i:D4}";
            var status = statuses[rand.Next(statuses.Length)];
            var amount = Math.Round((decimal)(rand.Next(1000, 100000)) / 100, 2);
            var account = $"DEMO_ACC_{(rand.Next(3) + 1)}";

            transactions.Add((refCode, account, amount, status));
        }

        return transactions;
    }

    private string GenerateSettlementCsv(
        List<(string ReferenceCode, string AccountId, decimal Amount, string Status)> transactions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("reference,amount,status");

        foreach (var tx in transactions)
        {
            sb.AppendLine($"{tx.ReferenceCode},{tx.Amount:F2},{tx.Status}");
        }

        return sb.ToString();
    }
}

public class SettlementAccuracyReport
{
    public string TestRunId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public int TotalTransactionsCreated { get; set; }
    public int SuccessfulTransactions { get; set; }
    public int PendingTransactions { get; set; }
    public int FailedTransactions { get; set; }
    public int LedgerEntriesPosted { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal TotalDebit { get; set; }
    public bool LedgerBalanced { get; set; }
    public string SettlementFileGenerated { get; set; } = string.Empty;
    public int Matched { get; set; }
    public int Mismatched { get; set; }
    public int Unknown { get; set; }
    public double MatchRate { get; set; }
    public List<object> SampleTransactions { get; set; } = new();
}
