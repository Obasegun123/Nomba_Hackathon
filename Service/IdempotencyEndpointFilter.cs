using StackExchange.Redis;

namespace Nomba_Hackathon.Service;

// Outbound idempotency guard for mutating endpoints. Clients send a stable
// "X-Idempotency-Key"; we use Redis SETNX so a retried request never triggers a
// second Nomba call. The first request holds a PROCESSING lock while it runs;
// once it completes the handler's reference is cached and replayed verbatim.
public class IdempotencyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Idempotency-Key";

    // Handlers stash the created reference here so it can be cached + replayed.
    public const string ReferenceItemKey = "idem-reference";

    private static readonly TimeSpan ProcessingTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DoneTtl = TimeSpan.FromHours(24);

    private readonly IConnectionMultiplexer _redis;

    public IdempotencyEndpointFilter(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var key = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            // No key supplied -> no idempotency guarantee, process normally.
            return await next(context);
        }

        var db = _redis.GetDatabase();
        var redisKey = $"idem:{key}";

        // Atomic SETNX: claim the key only if nobody else has. Redis calls are
        // wrapped in a retry policy so transient network faults (packet loss,
        // latency spikes) don't get misread as failures and drop the guard.
        bool acquired = await RedisResilience.Policy.ExecuteAsync(
            () => db.StringSetAsync(redisKey, "PROCESSING", ProcessingTtl, When.NotExists));
        if (!acquired)
        {
            var existing = await RedisResilience.Policy.ExecuteAsync(() => db.StringGetAsync(redisKey));
            if (existing == "PROCESSING")
            {
                // A first request with the same key is still in flight.
                return Results.Json(
                    new { status = "processing", idempotencyKey = key }, statusCode: 409);
            }

            // Completed earlier: replay the cached reference instead of re-calling Nomba.
            return Results.Ok(new { status = "duplicate", reference = existing.ToString() });
        }

        try
        {
            var result = await next(context);

            var reference = context.HttpContext.Items.TryGetValue(ReferenceItemKey, out var r)
                ? r?.ToString()
                : null;

            if (!string.IsNullOrEmpty(reference))
            {
                await RedisResilience.Policy.ExecuteAsync(() => db.StringSetAsync(redisKey, reference, DoneTtl));
            }
            else
            {
                // Nothing to cache (e.g. validation failure) -> release so retry works.
                await RedisResilience.Policy.ExecuteAsync(() => db.KeyDeleteAsync(redisKey));
            }

            return result;
        }
        catch
        {
            // Release the lock so the client can safely retry after a failure.
            await RedisResilience.Policy.ExecuteAsync(() => db.KeyDeleteAsync(redisKey));
            throw;
        }
    }
}
