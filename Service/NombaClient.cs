using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Nomba_Hackathon.Service;

// Typed HttpClient that talks to the Nomba REST API. Resilience (retry +
// circuit breaker) is layered on via Polly in Program.cs. OAuth
// client-credentials tokens are cached in Redis (shared across instances and
// across the transient typed-client lifetime) since they are valid for ~60m.
public class NombaClient : IPaymentProvider
{
    public string ProviderName => "Nomba";
    private const string TokenCacheKey = "nomba:access_token";
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(55);

    private readonly HttpClient _http;
    private readonly NombaOptions _options;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<NombaClient> _logger;

    public NombaClient(
        HttpClient http,
        IOptions<NombaOptions> options,
        IConnectionMultiplexer redis,
        ILogger<NombaClient> logger)
    {
        _http = http;
        _options = options.Value;
        _redis = redis;
        _logger = logger;
    }

    // Credentials are trimmed defensively in case of stray whitespace in config.
    private string AccountId => _options.AccountId.Trim();

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var cached = await db.StringGetAsync(TokenCacheKey);
        if (cached.HasValue)
        {
            return cached!;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/token/issue");
        request.Headers.TryAddWithoutValidation("accountId", AccountId);
        request.Content = JsonContent.Create(new
        {
            grant_type = "client_credentials",
            client_id = _options.ClientId.Trim(),
            client_secret = _options.ClientSecret.Trim()
        });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;

        var token = data.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("Nomba auth response had no access_token");

        // Cache shorter than the ~60m validity so we always refresh before expiry.
        await db.StringSetAsync(TokenCacheKey, token, TokenTtl);
        return token;
    }

    // Kept for source-compatibility; the shared type is ProviderTransactionStatus.

    // Creates a (reserved/dynamic) virtual account. Returns the upstream `data`
    // node (bankAccountNumber, bankName, accountRef, ...) for the API response.
    public Task<JsonNode?> CreateVirtualAccountAsync(object body, CancellationToken ct = default) =>
        PostAsync("/v1/accounts/virtual", body, ct);

    // Creates a hosted checkout order. Returns the upstream `data` node
    // (checkoutLink, orderReference, ...) for the API response.
    public Task<JsonNode?> CreateCheckoutOrderAsync(object body, CancellationToken ct = default) =>
        PostAsync("/v1/checkout/order", body, ct);

    // Lists the virtual accounts under the parent account. Useful for reading
    // existing accounts (the sandbox caps creation at 2) to find their NUBANs.
    // Nomba exposes this as POST /v1/accounts/virtual/list ("filter virtual
    // accounts"); an empty body returns every account, with results under
    // data.results and a data.cursor for pagination.
    public Task<JsonNode?> ListVirtualAccountsAsync(CancellationToken ct = default) =>
        PostAsync("/v1/accounts/virtual/list", new { }, ct);

    // Authenticated POST that returns the response `data` node as a JsonNode so
    // it can be relayed directly through Results.Ok without lifetime concerns.
    private async Task<JsonNode?> PostAsync(string path, object body, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("accountId", AccountId);

        using var response = await _http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Nomba POST {Path} returned {StatusCode}: {Body}", path, (int)response.StatusCode, payload);
            throw new HttpRequestException(
                $"Nomba POST {path} failed with status {(int)response.StatusCode}: {payload}");
        }

        var root = JsonNode.Parse(payload);
        return root?["data"] ?? root;
    }

    // Fetches the current status of a transaction by its order reference.
    // Returns the upstream status string (e.g. "SUCCESS", "PENDING", "FAILED")
    // or null when the transaction cannot be resolved.
    public async Task<string?> GetTransactionStatusAsync(string reference, CancellationToken ct = default) =>
        (await GetTransactionAsync(reference, ct)).Status;

    public async Task<ProviderTransactionStatus> GetTransactionAsync(string reference, CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/transactions/accounts/single?orderReference={Uri.EscapeDataString(reference)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("accountId", AccountId);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Nomba transaction lookup for {Reference} returned {StatusCode}",
                reference, (int)response.StatusCode);
            return new ProviderTransactionStatus(null, null);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
        var status = data.ValueKind == JsonValueKind.Object &&
                     data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        decimal? amount = data.ValueKind == JsonValueKind.Object &&
                          data.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number
            ? a.GetDecimal()
            : null;

        return new ProviderTransactionStatus(status, amount);
    }
}
