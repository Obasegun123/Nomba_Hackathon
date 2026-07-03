using Polly;
using Polly.Retry;
using StackExchange.Redis;

namespace Nomba_Hackathon.Service;

// Shared Polly retry pipeline for Redis command calls. Npgsql/EF Core has its
// own EnableRetryOnFailure; StackExchange.Redis has no equivalent for
// already-open-connection command timeouts/drops, so callers wrap individual
// commands (SETNX, GET, PING, ...) with this policy to survive transient
// network faults (packet loss, latency spikes) instead of failing the request.
public static class RedisResilience
{
    public static readonly AsyncRetryPolicy Policy = Polly.Policy
        .Handle<RedisTimeoutException>()
        .Or<RedisConnectionException>()
        .WaitAndRetryAsync(4, attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)));
}
