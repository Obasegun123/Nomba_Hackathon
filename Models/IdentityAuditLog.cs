namespace Nomba_Hackathon.Models;

public class IdentityAuditLog
{
    public Guid Id { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty; // "customer" or "virtual_account"
    public string FieldName { get; set; } = string.Empty; // "name", "email", "account_name"
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string ChangedBy { get; set; } = "system";
    public DateTimeOffset ChangedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
