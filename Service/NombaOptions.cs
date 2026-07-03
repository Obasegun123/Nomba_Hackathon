namespace Nomba_Hackathon.Service;

// Bound from the "Nomba" configuration section.
public class NombaOptions
{
    public string BaseUrl { get; set; } = "https://api.nomba.com";

    // Parent account ID — sent in the "accountId" header to authenticate.
    public string AccountId { get; set; } = string.Empty;

    // Sub-account that calls are scoped to (per Nomba's parent/sub model).
    public string SubAccountId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;
}
