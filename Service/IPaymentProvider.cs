using System.Text.Json.Nodes;

namespace Nomba_Hackathon.Service;

// Shared result type returned by all provider GetTransaction calls.
public sealed record ProviderTransactionStatus(string? Status, decimal? Amount);

// Abstraction over any payment aggregator (Nomba, Paystack, Flutterwave, …).
// NombaClient is the reference implementation. Adding a second provider means
// implementing this interface and registering it alongside the existing one.
public interface IPaymentProvider
{
    string ProviderName { get; }

    Task<JsonNode?> CreateVirtualAccountAsync(object body, CancellationToken ct = default);
    Task<JsonNode?> CreateCheckoutOrderAsync(object body, CancellationToken ct = default);
    Task<JsonNode?> ListVirtualAccountsAsync(CancellationToken ct = default);

    Task<ProviderTransactionStatus> GetTransactionAsync(string reference, CancellationToken ct = default);
    Task<string?> GetTransactionStatusAsync(string reference, CancellationToken ct = default);
}
