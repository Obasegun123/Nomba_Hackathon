namespace Nomba_Hackathon.Models;

// Maps to the "transactions" table (header) defined in SQL/Data.sql.
public class TransactionRecord
{
    public Guid Id { get; set; }

    public string? ReferenceCode { get; set; }

    // 'PENDING', 'SUCCESS', 'FAILED'
    public string Status { get; set; } = "PENDING";

    // Target account for the payment; populated on initiation so a PENDING
    // header is self-describing and reconciliation can post balanced entries.
    public string? AccountId { get; set; }

    // Expected/booked amount in kobo.
    public decimal Amount { get; set; }

    // Fuzzy matching fields
    public string? PayerName { get; set; }
    public decimal? MatchConfidence { get; set; }
    // 'EXACT_MATCH', 'FUZZY_MATCHED', or null if not applicable
    public string? MatchType { get; set; }

    // Partial payment tracking
    public Guid? PaymentPlanId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<LedgerEntry> Entries { get; set; } = new List<LedgerEntry>();
}
