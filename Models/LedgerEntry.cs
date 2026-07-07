namespace Nomba_Hackathon.Models;

// Maps to the "ledgerentries" table (double-entry) defined in SQL/Data.sql.
public class LedgerEntry
{
    public int Id { get; set; }

    public Guid TransactionId { get; set; }

    // Customer's virtual account or system account.
    public string AccountId { get; set; } = string.Empty;

    public decimal DebitAmount { get; set; }

    public decimal CreditAmount { get; set; }

    // 'PAYMENT', 'FEES', 'SETTLEMENT'
    public string EntryType { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public TransactionRecord? Transaction { get; set; }
}
