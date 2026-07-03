using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nomba_Hackathon.Data;
using Nomba_Hackathon.Models;
using Nomba_Hackathon.Service;
using Polly;
using Polly.Extensions.Http;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var pgConn = config.GetConnectionString("Postgres")
             ?? "Host=localhost;Port=5432;Database=nomba_ledger;Username=admin;Password=securepassword123";
var redisConn = config.GetConnectionString("Redis") ?? "localhost:6379";

builder.Services.Configure<NombaOptions>(config.GetSection("Nomba"));

// OpenAPI / Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new()
{
    Title = "NexusLedger API",
    Version = "v1",
    Description = "Atomic Reconciliation & Settlement Engine for Nomba integrations."
}));

// Data layer. EnableRetryOnFailure survives transient network faults (dropped
// packets, brief latency spikes) between the app and Postgres instead of
// bubbling a fatal exception up to the request. Callers that open an explicit
// transaction (PaymentService.SettleAsync) must run it through
// Database.CreateExecutionStrategy() — EF Core forbids user transactions
// under a retrying strategy otherwise.
builder.Services.AddDbContext<LedgerDbContext>(o => o.UseNpgsql(pgConn,
    np => np.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null)));

// Redis + idempotency. AbortOnConnectFail=false + ConnectRetry let the
// multiplexer ride out a Redis blip instead of poisoning itself; individual
// commands are further wrapped in RedisResilience.Policy at the call sites.
var redisOptions = ConfigurationOptions.Parse(redisConn);
redisOptions.AbortOnConnectFail = false;
redisOptions.ConnectRetry = 5;
redisOptions.ConnectTimeout = 5000;
redisOptions.SyncTimeout = 5000;
redisOptions.ReconnectRetryPolicy = new ExponentialRetry(1000, 10000);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();

// Ledger + reconciliation services
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddScoped<LedgerQueryService>();

// Multi-provider abstraction: NombaClient is the reference implementation.
// Register additional IPaymentProvider implementations here to add providers.
builder.Services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<NombaClient>());

// Nomba typed HttpClient with Polly resilience (retry + circuit breaker)
builder.Services.AddHttpClient<NombaClient>(c =>
        c.BaseAddress = new Uri(config["Nomba:BaseUrl"] ?? "https://api.nomba.com"))
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt))))
    .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
        .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)));

// Hangfire (PostgreSQL storage) + worker
builder.Services.AddHangfire(h => h
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(pgConn)));
// Hangfire:DisableServer lets an instance accept/enqueue jobs without any
// worker draining the queue — used by the "mid-transaction death" chaos test
// to deterministically strand an enqueued settlement job, then prove a normal
// restart (server re-enabled) picks it up and completes it with no data loss.
if (!string.Equals(config["Hangfire:DisableServer"], "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHangfireServer();
}

// Retries transient Hangfire.PostgreSql connection faults hit while enqueuing
// the settlement job off the webhook (see call site for why this is needed
// on top of EnableRetryOnFailure).
var hangfireEnqueueRetry = Policy
    .Handle<Hangfire.BackgroundJobClientException>()
    .WaitAndRetryAsync(4, attempt => TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt)));

var app = builder.Build();

// Provision the ledger schema on first run if the tables are absent, so a fresh
// database (or recreated Docker volume) works without a manual psql step.
await DatabaseInitializer.EnsureSchemaAsync(pgConn, app.Environment, app.Logger);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Skip HTTPS redirection in Development so plain-HTTP webhook POSTs tunnelled
// via ngrok aren't answered with a 307 (which Nomba won't follow).
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseHangfireDashboard("/hangfire");

// Recurring background reconciliation every 5 minutes.
RecurringJob.AddOrUpdate<ReconciliationService>(
    "reconcile-pending", svc => svc.ReconcilePendingAsync(), "*/5 * * * *");

app.MapPost("/webhooks/nomba", async (
    HttpContext context,
    IOptions<NombaOptions> opts,
    IIdempotencyService idempotency,
    IBackgroundJobClient jobs) =>
{
    var signature = context.Request.Headers["nomba-signature"].ToString();
    var timestamp = context.Request.Headers["nomba-timestamp"].ToString();
    var secret = opts.Value.WebhookSecret;

    context.Request.EnableBuffering();
    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync();
    context.Request.Body.Position = 0;

    // Nomba probes the URL with an empty, unsigned POST to confirm it is live
    // before it will deliver real (signed) events. Acknowledge it with 200 so
    // the webhook is accepted; an empty body has nothing to verify or book.
    if (string.IsNullOrEmpty(body))
    {
        app.Logger.LogInformation("Nomba webhook validation ping acknowledged");
        return Results.Ok(new { status = "ok" });
    }

    if (!VerifyNombaSignature(body, signature, secret, timestamp))
    {
        // Diagnostic only — never logs the secret or the signature value. Tells
        // a missing header (sigLen=0) apart from a value/secret mismatch.
        app.Logger.LogWarning(
            "Invalid Nomba signature: header '{Header}' present={Present} sigLen={SigLen} bodyLen={BodyLen} secretConfigured={SecretSet}",
            "nomba-signature",
            !string.IsNullOrEmpty(signature),
            signature.Length,
            body.Length,
            !string.IsNullOrEmpty(secret));
        return Results.Unauthorized();
    }

    var (eventType, requestId) = ParseEnvelope(body);

    // Idempotency: drop re-delivered events that we have already accepted.
    if (await idempotency.IsDuplicateEventAsync(requestId ?? signature))
    {
        app.Logger.LogInformation("Duplicate Nomba event {RequestId} ignored", requestId);
        return Results.Ok(new { status = "duplicate_ignored" });
    }

    app.Logger.LogInformation("Nomba webhook verified: {EventType}", eventType);

    // Only money-in events credit the ledger (payment_success,
    // virtual_account.funded, mandate.debit_success). Outbound transfers
    // (transfer.success/failed) settle money OUT and are never booked here.
    if (IsMoneyInEvent(eventType))
    {
        // Hangfire.PostgreSql opens its own raw Npgsql connection to persist the
        // job row — it is NOT covered by the EF Core EnableRetryOnFailure
        // strategy (that only wraps LedgerDbContext operations). Without this,
        // a transient DB blip during enqueue throws BackgroundJobClientException
        // straight through to the webhook caller instead of being retried.
        await hangfireEnqueueRetry.ExecuteAsync(() =>
        {
            jobs.Enqueue<PaymentService>(svc => svc.ProcessSuccessfulPayment(body));
            return Task.CompletedTask;
        });
    }

    return Results.Ok(new { status = "accepted" });
})
.WithTags("Webhooks")
.WithName("HandleNombaWebhook")
.WithSummary("Receive and verify a signed Nomba payment webhook. Idempotent — re-deliveries are ignored.");

// Phase 1 — initiate a payment into a (reserved) virtual account. Persists a
// PENDING header so the credit webhook (or reconciliation) can settle it later.
app.MapPost("/payments/virtual-account", async (
    VirtualAccountRequest req,
    HttpContext http,
    NombaClient nomba,
    PaymentService payments,
    LedgerDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.AccountRef) || string.IsNullOrWhiteSpace(req.AccountName))
        return Results.BadRequest(new { error = "accountRef and accountName are required" });

    var body = new Dictionary<string, object?>
    {
        ["accountRef"] = req.AccountRef,
        ["accountName"] = req.AccountName,
        ["currency"] = "NGN"
    };
    // Lock the expected amount upstream so the credit webhook echoes amountExpected.
    if (req.Amount > 0) body["amount"] = req.Amount;

    var account = await nomba.CreateVirtualAccountAsync(body);

    // Register in the local account registry (upsert: update name if already exists).
    var existing = await db.VirtualAccounts.FindAsync(req.AccountRef);
    if (existing is null)
    {
        db.VirtualAccounts.Add(new VirtualAccount
        {
            AccountRef = req.AccountRef,
            AccountName = req.AccountName,
            Status = "ACTIVE",
            KycTier = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }
    else
    {
        existing.AccountName = req.AccountName;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
    }
    await db.SaveChangesAsync();

    await payments.CreatePendingAsync(req.AccountRef, req.AccountRef, req.Amount);
    http.Items[IdempotencyEndpointFilter.ReferenceItemKey] = req.AccountRef;

    return Results.Ok(new { reference = req.AccountRef, status = "PENDING", account });
})
.WithTags("Payments").WithName("CreateVirtualAccount")
.WithSummary("Provision a dedicated virtual account tied to a customer identity. Idempotent via X-Idempotency-Key.")
.AddEndpointFilter<ApiKeyEndpointFilter>()
.AddEndpointFilter<IdempotencyEndpointFilter>();

// Lists the virtual accounts already provisioned on the Nomba account. Handy
// when the sandbox 2-account cap is hit and you need an existing NUBAN to fund.
app.MapGet("/payments/virtual-accounts", async (NombaClient nomba) =>
    Results.Ok(await nomba.ListVirtualAccountsAsync()))
.WithTags("Payments").WithName("ListVirtualAccounts")
.WithSummary("List all virtual accounts provisioned under the parent Nomba account.")
.AddEndpointFilter<ApiKeyEndpointFilter>();

// Phase 1 — initiate a hosted checkout order. Persists a PENDING header keyed by
// the order reference; the payment.success webhook (or reconciliation) settles it.
app.MapPost("/payments/checkout", async (
    CheckoutRequest req,
    HttpContext http,
    NombaClient nomba,
    PaymentService payments) =>
{
    if (string.IsNullOrWhiteSpace(req.OrderReference) || string.IsNullOrWhiteSpace(req.CustomerEmail))
        return Results.BadRequest(new { error = "orderReference and customerEmail are required" });

    var checkout = await nomba.CreateCheckoutOrderAsync(new
    {
        order = new
        {
            orderReference = req.OrderReference,
            customerEmail = req.CustomerEmail,
            amount = req.Amount,
            currency = "NGN",
            callbackUrl = req.CallbackUrl
        }
    });

    await payments.CreatePendingAsync(req.OrderReference, req.CustomerEmail, req.Amount);
    http.Items[IdempotencyEndpointFilter.ReferenceItemKey] = req.OrderReference;

    return Results.Ok(new { reference = req.OrderReference, status = "PENDING", checkout });
})
.WithTags("Payments").WithName("CreateCheckoutOrder")
.WithSummary("Initiate a hosted checkout order. Idempotent via X-Idempotency-Key.")
.AddEndpointFilter<ApiKeyEndpointFilter>()
.AddEndpointFilter<IdempotencyEndpointFilter>();

// Returns identity, status, and KYC tier for a registered virtual account.
app.MapGet("/account/{id}", async (string id, LedgerDbContext db) =>
{
    var account = await db.VirtualAccounts.FindAsync(id);
    return account is null
        ? Results.NotFound(new { error = "Account not found" })
        : Results.Ok(account);
})
.WithTags("Accounts").WithName("GetAccount")
.WithSummary("Get virtual account details: identity, status (ACTIVE/CLOSED/SUSPENDED), and KYC tier.")
.AddEndpointFilter<ApiKeyEndpointFilter>();

// Update an account's name, status, and/or KYC tier. All fields are optional.
// status:  ACTIVE | CLOSED | SUSPENDED
// kycTier: 1 (₦50k/day) | 2 (₦200k/day) | 3 (unlimited)
app.MapMethods("/account/{id}", ["PATCH"], async (
    string id,
    UpdateAccountRequest req,
    LedgerDbContext db) =>
{
    var account = await db.VirtualAccounts.FindAsync(id);
    if (account is null)
        return Results.NotFound(new { error = "Account not found" });

    var updated = new List<string>();

    if (!string.IsNullOrWhiteSpace(req.AccountName) && req.AccountName != account.AccountName)
    {
        account.AccountName = req.AccountName;
        updated.Add("accountName");
    }

    if (!string.IsNullOrWhiteSpace(req.Status))
    {
        if (req.Status is not ("ACTIVE" or "CLOSED" or "SUSPENDED"))
            return Results.BadRequest(new { error = "status must be ACTIVE, CLOSED, or SUSPENDED" });
        account.Status = req.Status;
        updated.Add("status");
    }

    if (req.KycTier is not null)
    {
        if (req.KycTier is < 1 or > 3)
            return Results.BadRequest(new { error = "kycTier must be 1, 2, or 3" });
        account.KycTier = req.KycTier.Value;
        updated.Add("kycTier");
    }

    account.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(new { accountRef = id, updated, account });
})
.WithTags("Accounts").WithName("UpdateAccount")
.WithSummary("Rename an account, change its status (close/suspend/reactivate), or upgrade its KYC tier.")
.AddEndpointFilter<ApiKeyEndpointFilter>();

app.MapGet("/account/{id}/balance", async (string id, LedgerQueryService queries) =>
    Results.Ok(await queries.GetBalanceAsync(id)))
.WithTags("Accounts").WithName("GetBalance")
.WithSummary("Live balance computed from the double-entry ledger: totalCredit - totalDebit.")
.AddEndpointFilter<ApiKeyEndpointFilter>();

app.MapGet("/transactions", async (
    LedgerQueryService queries, string? status, string? accountId, int page = 1, int pageSize = 50) =>
    Results.Ok(await queries.GetTransactionsAsync(status, accountId, page, pageSize)))
.WithTags("Reporting").WithName("ListTransactions")
.WithSummary("Paged transaction history. Filter by status (PENDING/SUCCESS/FAILED/OVERPAYMENT/UNDERPAYMENT/MISDIRECTED/KYC_LIMIT_EXCEEDED) or accountId. KYC_LIMIT_EXCEEDED fires when a tier-1 account exceeds ₦50k/day or tier-2 exceeds ₦200k/day.")
.AddEndpointFilter<ApiKeyEndpointFilter>();

// Settlement file reconciliation: accepts a CSV (reference,amount,status) from a
// payment provider's settlement report and matches each row against the internal
// ledger. Returns a structured report of matched, mismatched, and unknown rows
// — the "zero-mismatch reconciliation" proof from the project brief.
app.MapPost("/reconcile/settlement", async (HttpRequest request, LedgerDbContext db) =>
{
    using var reader = new StreamReader(request.Body);
    var csv = await reader.ReadToEndAsync();

    var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                   .Select(l => l.Trim())
                   .Where(l => !string.IsNullOrEmpty(l))
                   .ToList();

    if (lines.Count == 0)
        return Results.BadRequest(new { error = "Empty settlement file" });

    // Skip header row if present.
    var start = lines[0].StartsWith("reference", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    var matched   = new List<object>();
    var mismatched = new List<object>();
    var unknown   = new List<object>();

    for (var i = start; i < lines.Count; i++)
    {
        var parts = lines[i].Split(',');
        if (parts.Length < 2) continue;

        var reference      = parts[0].Trim();
        var providerAmount = decimal.TryParse(parts[1].Trim(), out var a) ? a : (decimal?)null;
        var providerStatus = parts.Length > 2 ? parts[2].Trim().ToUpperInvariant() : null;

        var tx = await db.Transactions.FirstOrDefaultAsync(t => t.ReferenceCode == reference);

        if (tx is null)
        {
            unknown.Add(new { reference, providerAmount, providerStatus, issue = "not_found" });
            continue;
        }

        var amountMatch  = providerAmount is null || providerAmount == tx.Amount;
        var statusMatch  = providerStatus is null || providerStatus == tx.Status ||
                           (providerStatus == "SUCCESS" && tx.Status is "SUCCESS" or "OVERPAYMENT" or "UNDERPAYMENT");

        if (amountMatch && statusMatch)
        {
            matched.Add(new { reference, ledgerStatus = tx.Status, ledgerAmount = tx.Amount });
        }
        else
        {
            mismatched.Add(new
            {
                reference,
                ledgerStatus = tx.Status, providerStatus,
                ledgerAmount = tx.Amount, providerAmount,
                issues = new[]
                {
                    !amountMatch ? "amount_mismatch" : null,
                    !statusMatch ? "status_mismatch" : null
                }.Where(x => x is not null)
            });
        }
    }

    var total = matched.Count + mismatched.Count + unknown.Count;
    return Results.Ok(new
    {
        summary = new
        {
            total,
            matched   = matched.Count,
            mismatched = mismatched.Count,
            unknown   = unknown.Count,
            matchRate = total > 0 ? Math.Round((double)matched.Count / total * 100, 1) : 0
        },
        matched,
        mismatched,
        unknown,
        reconciledAt = DateTimeOffset.UtcNow
    });
})
.WithTags("Reconciliation").WithName("UploadSettlementFile")
.WithSummary("Match a provider settlement CSV (reference,amount,status) against the internal ledger. Returns matched, mismatched, and unknown rows with a match-rate percentage.")
.AddEndpointFilter<ApiKeyEndpointFilter>();

app.MapGet("/health", async (LedgerDbContext db, IConnectionMultiplexer redis) =>
{
    var postgresOk = await db.Database.CanConnectAsync();

    bool redisOk;
    try
    {
        await redis.GetDatabase().PingAsync();
        redisOk = true;
    }
    catch
    {
        redisOk = false;
    }

    var payload = new { status = postgresOk && redisOk ? "healthy" : "degraded", postgres = postgresOk, redis = redisOk };
    return postgresOk && redisOk ? Results.Ok(payload) : Results.Json(payload, statusCode: 503);
})
.WithTags("System").WithName("Healthcheck")
.WithSummary("Liveness probe: confirms Postgres and Redis connectivity. Returns 503 when either dependency is down.");

// Business + system observability: transaction mix, double-entry balance
// invariant (credits - debits == 0), reconciliation backlog, dependency liveness.
app.MapGet("/metrics", async (LedgerQueryService queries, IConnectionMultiplexer redis) =>
{
    var m = await queries.GetMetricsAsync(redis);
    return Results.Ok(new
    {
        transactions = new { total = m.ByStatus.Values.Sum(), byStatus = m.ByStatus },
        ledger       = new { m.TotalCredit, m.TotalDebit, m.LedgerBalance, m.Balanced, entries = m.EntryCount },
        reconciliation = new { pending = m.PendingCount },
        system       = new { postgres = m.PostgresOk, redis = m.RedisOk },
        generatedAt  = DateTimeOffset.UtcNow
    });
})
.WithTags("Reporting").WithName("GetMetrics")
.WithSummary("Audit-ready ledger metrics: transaction mix, double-entry balance invariant (balanced=true proves credits==debits), and reconciliation backlog.");

app.Run();

static (string? EventType, string? RequestId) ParseEnvelope(string body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var eventType = root.TryGetProperty("event_type", out var et) ? et.GetString()
            : (root.TryGetProperty("event", out var ev) ? ev.GetString() : null);
        var requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
        return (eventType, requestId);
    }
    catch (JsonException)
    {
        return (null, null);
    }
}

// Classifies a Nomba event as a customer/merchant credit (money in). Outbound
// transfers are explicitly excluded so they are never booked as a payment.
static bool IsMoneyInEvent(string? eventType)
{
    if (string.IsNullOrEmpty(eventType)) return false;
    var e = eventType.ToLowerInvariant();
    if (e.StartsWith("transfer")) return false;

    return (e.Contains("payment") && e.Contains("success")) // payment_success
        || e.Contains("funded")                              // virtual_account.funded
        || e.Contains("credit")                              // virtual.account.credit (legacy)
        || (e.Contains("debit") && e.Contains("success"));   // mandate.debit_success
}

// Nomba signs webhooks by concatenating specific transaction fields (not the raw
// body) and HMAC-SHA256-ing that string with the webhook secret. The timestamp
// comes from the nomba-timestamp request header.
// Signing string: event_type:request_id:user_id:wallet_id:transaction_id:transaction_type:transaction_time:response_code:timestamp
// https://developer.nomba.com/docs/api-basics/webhook
static bool VerifyNombaSignature(string body, string signature, string secret, string timestamp)
{
    if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d : default;

        static string Field(JsonElement el, params string[] keys)
        {
            foreach (var k in keys)
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var v))
                    return v.GetString() ?? string.Empty;
            return string.Empty;
        }

        // Real payment_success payload nests merchant + transaction under data.
        var merchant = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("merchant", out var m) ? m : default;
        var tx       = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("transaction", out var t) ? t : default;

        var eventType = Field(root,     "event_type", "event");
        var requestId = Field(root,     "requestId",  "request_id");
        var userId    = Field(merchant, "userId",     "user_id");
        var walletId  = Field(merchant, "walletId",   "wallet_id");
        var txId      = Field(tx,       "transactionId",   "transaction_id");
        var txType    = Field(tx,       "type",            "transactionType", "transaction_type");
        var txTime    = Field(tx,       "time",            "transactionTime", "transaction_time");
        var respCode  = Field(tx,       "responseCode",    "response_code");
        if (respCode == "null") respCode = string.Empty;

        var signingString = $"{eventType}:{requestId}:{userId}:{walletId}:{txId}:{txType}:{txTime}:{respCode}:{timestamp}";

        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(keyBytes);
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingString)));

        return FixedTimeEquals(signature, computed);
    }
    catch (JsonException)
    {
        return false;
    }
}

static bool FixedTimeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(a),
        Encoding.UTF8.GetBytes(b));