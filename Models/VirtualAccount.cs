namespace Nomba_Hackathon.Models;

public class VirtualAccount
{
    public string AccountRef { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    // ACTIVE | CLOSED | SUSPENDED
    public string Status { get; set; } = "ACTIVE";
    // 1 = ₦50k/day limit, 2 = ₦200k/day limit, 3 = unlimited
    public int KycTier { get; set; } = 1;
    // Bank account details from Nomba
    public string? Nuban { get; set; }
    public string? BankCode { get; set; }
    public string? BankName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Customer? Customer { get; set; }
}
