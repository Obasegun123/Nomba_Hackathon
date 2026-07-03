using StackExchange.Redis;

namespace Nomba_Hackathon.Service;

public interface IIdempotencyService
{
    Task<bool> IsDuplicateEventAsync(string requestId);
}

// Uses Redis SETNX (StringSet with When.NotExists) to guarantee that a given
// webhook event is processed only once, even if Nomba re-delivers it.
public class IdempotencyService : IIdempotencyService
{
    private readonly IConnectionMultiplexer _redis;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public IdempotencyService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<bool> IsDuplicateEventAsync(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            // Without a stable id we cannot dedupe; treat as new to avoid dropping events.
            return false;
        }

        var db = _redis.GetDatabase();

        // Set the key only if it does not already exist (atomic SETNX with TTL).
        // Wrapped in a retry policy so transient Redis network faults don't get
        // misread as "not a duplicate" and double-book the ledger.
        bool isNew = await RedisResilience.Policy.ExecuteAsync(() => db.StringSetAsync(
            $"nomba_event:{requestId}",
            "processed",
            Ttl,
            When.NotExists));

        return !isNew; // If we could not set it, the key existed => duplicate.
    }
}
