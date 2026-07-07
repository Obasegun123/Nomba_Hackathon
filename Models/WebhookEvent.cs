namespace Nomba_Hackathon.Models;

public class WebhookEvent
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string AccountRef { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? PayloadJson { get; set; }
    public int RetryCount { get; set; }
    // PENDING | DELIVERED | FAILED
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
}

public class WebhookSubscription
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    // ACTIVE | DISABLED
    public string Status { get; set; } = "ACTIVE";
    public string? Secret { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}
