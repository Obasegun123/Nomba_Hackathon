namespace Nomba_Hackathon.Models;

public class KycTierAuditLog
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public int OldTier { get; set; }
    public int NewTier { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = "system";
    public DateTimeOffset ChangedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Customer? Customer { get; set; }
}
