using Microsoft.Extensions.Options;

namespace Whisper.Server.RateLimiting;

/// <summary>
/// The hub's three budgets. They are separate because the abuse they guard against is
/// different in kind: chat spam is keyed per connection, password guessing is keyed per
/// source address (a new connection per attempt is free), and everything else is a
/// catch-all so a scripted client cannot pin the server by hammering mute or join.
/// </summary>
public sealed class HubRateLimits
{
    public HubRateLimits(TimeProvider timeProvider, IOptions<WhisperServerOptions> options)
    {
        var settings = options.Value;

        Chat = new TokenBucketRateLimiter(
            timeProvider,
            settings.ChatRateLimitBurst,
            TimeSpan.FromSeconds(settings.ChatRateLimitWindowSeconds));

        Auth = new TokenBucketRateLimiter(
            timeProvider,
            settings.AuthRateLimitBurst,
            TimeSpan.FromSeconds(settings.AuthRateLimitWindowSeconds));

        Actions = new TokenBucketRateLimiter(
            timeProvider,
            settings.ActionRateLimitBurst,
            TimeSpan.FromSeconds(settings.ActionRateLimitWindowSeconds));
    }

    public IRateLimiter Chat { get; }

    public IRateLimiter Auth { get; }

    public IRateLimiter Actions { get; }

    /// <summary>Releases a disconnected connection's buckets so they cannot accumulate.</summary>
    public void Forget(string connectionId)
    {
        Chat.Forget(connectionId);
        Actions.Forget(connectionId);
    }
}
