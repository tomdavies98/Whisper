using System.Collections.Concurrent;

namespace Whisper.Server.RateLimiting;

public interface IRateLimiter
{
    bool TryAcquire(string key);

    void Forget(string key);
}

/// <summary>
/// Per-key token bucket. Refills continuously rather than in fixed windows so a client
/// cannot double its allowance by straddling a window boundary.
/// </summary>
public sealed class TokenBucketRateLimiter(TimeProvider timeProvider, int burst, TimeSpan window) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly double _tokensPerTick = burst / (double)Math.Max(window.Ticks, 1);

    public bool TryAcquire(string key)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(burst, now));

        lock (bucket)
        {
            var elapsed = now - bucket.LastRefillTicks;
            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(burst, bucket.Tokens + (elapsed * _tokensPerTick));
                bucket.LastRefillTicks = now;
            }

            if (bucket.Tokens < 1d)
            {
                return false;
            }

            bucket.Tokens -= 1d;
            return true;
        }
    }

    public void Forget(string key) => _buckets.TryRemove(key, out _);

    private sealed class Bucket(double tokens, long lastRefillTicks)
    {
        public double Tokens { get; set; } = tokens;

        public long LastRefillTicks { get; set; } = lastRefillTicks;
    }
}
