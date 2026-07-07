namespace Nomba_Hackathon.Models;

// Body for POST /payments/virtual-account. Amount is the expected payment in
// kobo: it is forwarded to Nomba to lock the expected amount and stored on the
// PENDING header for over/under detection when the credit webhook arrives.
public record VirtualAccountRequest(
    string AccountRef,
    string AccountName,
    decimal Amount);

// Body for POST /payments/checkout. Amount is the order amount in kobo.
public record CheckoutRequest(
    string OrderReference,
    decimal Amount,
    string CustomerEmail,
    string? CallbackUrl);

// Body for PATCH /account/{id}. All fields are optional; only supplied fields are updated.
// Status: ACTIVE | CLOSED | SUSPENDED
// KycTier: 1 (₦50k/day) | 2 (₦200k/day) | 3 (unlimited)
public record UpdateAccountRequest(
    string? AccountName,
    string? Status,
    int? KycTier);

// Body for POST /customers
public record CreateCustomerRequest(
    string Name,
    string Email,
    string? PhoneNumber,
    int? KycTier);

// Body for PATCH /customers/{id}
public record UpdateCustomerRequest(
    string? Name,
    int? KycTier,
    string? Status,
    string? KycTierReason);

// Body for POST /virtual-accounts (customer-linked provisioning)
public record CreateVirtualAccountRequest(
    string CustomerId,
    string AccountName,
    decimal? Amount);

// Body for PATCH /misdirected-payments/{id}/resolve
public record ResolveMisdirectedRequest(
    string ResolutionNote);

// Body for POST /webhooks/subscribe
public record CreateWebhookSubscriptionRequest(
    string Url,
    string? Secret);
