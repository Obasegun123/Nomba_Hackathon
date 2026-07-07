namespace Nomba_Hackathon.Models;

public class Customer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public int KycTier { get; set; } = 1;
    public decimal DailyLimit { get; set; } = 50000m;
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<VirtualAccount> VirtualAccounts { get; set; } = new List<VirtualAccount>();
}
