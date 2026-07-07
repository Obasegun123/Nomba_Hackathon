namespace Nomba_Hackathon.Models;

public class PaymentPlan
{
    public Guid Id { get; set; }
    public string TransactionRef { get; set; } = string.Empty;
    public string AccountRef { get; set; } = string.Empty;
    public decimal TotalExpected { get; set; }
    public decimal TotalReceived { get; set; }
    public decimal RemainingBalance { get; set; }
    // PENDING | COMPLETED | ABANDONED
    public string Status { get; set; } = "PENDING";
    public int InstallmentsReceived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastPaymentAt { get; set; }
}
