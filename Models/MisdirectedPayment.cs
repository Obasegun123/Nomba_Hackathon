namespace Nomba_Hackathon.Models;

public class MisdirectedPayment
{
    public Guid Id { get; set; }
    public string TransactionRef { get; set; } = string.Empty;
    public string ReceivedAccountRef { get; set; } = string.Empty;
    public string? IntendedCustomerId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    // PENDING | RETURNED | HELD | RESOLVED
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
}
