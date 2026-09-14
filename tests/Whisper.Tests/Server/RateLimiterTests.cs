using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whisper.Server;
using Whisper.Server.RateLimiting;
using Xunit;

namespace Whisper.Tests.Server;

public class TokenBucketRateLimiterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly TokenBucketRateLimiter _limiter;

    public TokenBucketRateLimiterTests() =>
        _limiter = new TokenBucketRateLimiter(_time, burst: 5, window: TimeSpan.FromSeconds(10));

    [Fact]
    public void TryAcquire_UpToTheBurst_Succeeds()
    {
        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a").Should().BeTrue();
        }
    }

    [Fact]
    public void TryAcquire_BeyondTheBurst_IsRefused()
    {
        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a");
        }

        _limiter.TryAcquire("a").Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_RefillsGraduallyRatherThanInWindowJumps()
    {
        // Fixed windows let a caller spend two full bursts across a boundary.
        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a");
        }

        _time.Advance(TimeSpan.FromSeconds(2));

        _limiter.TryAcquire("a").Should().BeTrue();
        _limiter.TryAcquire("a").Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_AfterAFullWindow_RestoresTheWholeBurst()
    {
        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a");
        }

        _time.Advance(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a").Should().BeTrue();
        }
    }

    [Fact]
    public void TryAcquire_DoesNotAccumulateBeyondTheBurst()
    {
        _time.Advance(TimeSpan.FromHours(1));

        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a").Should().BeTrue();
        }

        _limiter.TryAcquire("a").Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_KeepsBudgetsIndependentPerKey()
    {
        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a");
        }

        _limiter.TryAcquire("b").Should().BeTrue();
    }

    [Fact]
    public void Forget_ReleasesTheKeySoItDoesNotLeakPerConnection()
    {
        _limiter.TryAcquire("a");

        _limiter.Forget("a");

        // A fresh bucket is correct here: the key is only forgotten on disconnect.
        for (var i = 0; i < 5; i++)
        {
            _limiter.TryAcquire("a").Should().BeTrue();
        }
    }
}

public class HubRateLimitsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

    private HubRateLimits Build(WhisperServerOptions? options = null) =>
        new(_time, Options.Create(options ?? new WhisperServerOptions()));

    [Fact]
    public void Limits_AreConfiguredFromOptions()
    {
        var limits = Build(new WhisperServerOptions
        {
            ChatRateLimitBurst = 2,
            AuthRateLimitBurst = 1,
            ActionRateLimitBurst = 3,
        });

        CountAcquires(limits.Chat).Should().Be(2);
        CountAcquires(limits.Auth).Should().Be(1);
        CountAcquires(limits.Actions).Should().Be(3);
    }

    [Fact]
    public void Limits_AreSeparateBudgets()
    {
        // Spamming chat must not lock the user out of muting themselves.
        var limits = Build(new WhisperServerOptions { ChatRateLimitBurst = 1 });
        limits.Chat.TryAcquire("conn-1");

        limits.Chat.TryAcquire("conn-1").Should().BeFalse();
        limits.Actions.TryAcquire("conn-1").Should().BeTrue();
    }

    [Fact]
    public void Forget_ReleasesTheConnectionsChatAndActionBuckets()
    {
        var limits = Build(new WhisperServerOptions { ChatRateLimitBurst = 1, ActionRateLimitBurst = 1 });
        limits.Chat.TryAcquire("conn-1");
        limits.Actions.TryAcquire("conn-1");

        limits.Forget("conn-1");

        limits.Chat.TryAcquire("conn-1").Should().BeTrue();
        limits.Actions.TryAcquire("conn-1").Should().BeTrue();
    }

    [Fact]
    public void Forget_LeavesTheAuthBudgetAlone()
    {
        // Auth is keyed by address, so dropping the connection must not reset it or
        // reconnecting would hand out unlimited password guesses.
        var limits = Build(new WhisperServerOptions { AuthRateLimitBurst = 1 });
        limits.Auth.TryAcquire("10.0.0.5");

        limits.Forget("10.0.0.5");

        limits.Auth.TryAcquire("10.0.0.5").Should().BeFalse();
    }

    private static int CountAcquires(IRateLimiter limiter)
    {
        var granted = 0;

        while (limiter.TryAcquire("key") && granted < 1000)
        {
            granted++;
        }

        return granted;
    }
}
