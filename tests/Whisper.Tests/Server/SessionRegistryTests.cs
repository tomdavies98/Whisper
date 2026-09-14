using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Whisper.Server.Voice;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Server;

public class SessionRegistryTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionRegistry _sessions;

    public SessionRegistryTests() => _sessions = new SessionRegistry(_time);

    private static IPEndPoint Endpoint(int port = 5000) => new(IPAddress.Loopback, port);

    [Fact]
    public void Add_GivesEachSessionADistinctSsrcAndVoiceToken()
    {
        var first = _sessions.Add("conn-1", Guid.NewGuid(), "First");
        var second = _sessions.Add("conn-2", Guid.NewGuid(), "Second");

        first.Ssrc.Should().NotBe(second.Ssrc);
        first.VoiceToken.Should().NotBe(second.VoiceToken);
        first.Ssrc.Should().NotBe(0u);
        _sessions.Count.Should().Be(2);
    }

    [Fact]
    public void Add_StartsWithNoVoiceBinding()
    {
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "First");

        session.RemoteEndPoint.Should().BeNull();
        session.VoiceChannelId.Should().BeNull();
    }

    [Fact]
    public void TryBindEndpoint_WithTheIssuedToken_PinsTheEndpoint()
    {
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "First");

        _sessions.TryBindEndpoint(session.VoiceToken, Endpoint(), out var bound).Should().BeTrue();

        bound.Should().BeSameAs(session);
        session.RemoteEndPoint.Should().Be(Endpoint());
    }

    [Fact]
    public void TryBindEndpoint_WithAnUnknownToken_ReturnsFalse()
    {
        _sessions.Add("conn-1", Guid.NewGuid(), "First");

        _sessions.TryBindEndpoint(Guid.NewGuid(), Endpoint(), out _).Should().BeFalse();
    }

    [Fact]
    public void EvictExpiredVoiceBindings_AfterTheKeepaliveWindow_DropsTheBinding()
    {
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "First");
        _sessions.TryBindEndpoint(session.VoiceToken, Endpoint(), out _);
        session.VoiceChannelId = 1;

        _time.Advance(ProtocolLimits.VoiceSessionTimeout + TimeSpan.FromSeconds(1));
        var evicted = _sessions.EvictExpiredVoiceBindings(ProtocolLimits.VoiceSessionTimeout);

        evicted.Should().ContainSingle().Which.Should().BeSameAs(session);
        session.RemoteEndPoint.Should().BeNull();
        session.VoiceChannelId.Should().BeNull();
    }

    [Fact]
    public void EvictExpiredVoiceBindings_LeavesTheChatSessionConnected()
    {
        // Losing voice must not disconnect the member from chat: SignalR owns that lifetime.
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "First");
        _sessions.TryBindEndpoint(session.VoiceToken, Endpoint(), out _);

        _time.Advance(ProtocolLimits.VoiceSessionTimeout + TimeSpan.FromSeconds(1));
        _sessions.EvictExpiredVoiceBindings(ProtocolLimits.VoiceSessionTimeout);

        _sessions.TryGetByConnection("conn-1", out var stillThere).Should().BeTrue();
        stillThere.Should().BeSameAs(session);
        _sessions.Count.Should().Be(1);
    }

    [Fact]
    public void EvictExpiredVoiceBindings_WithARecentKeepalive_KeepsTheBinding()
    {
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "First");
        _sessions.TryBindEndpoint(session.VoiceToken, Endpoint(), out _);

        _time.Advance(TimeSpan.FromSeconds(10));
        _sessions.Touch(session);
        _time.Advance(TimeSpan.FromSeconds(10));

        _sessions.EvictExpiredVoiceBindings(ProtocolLimits.VoiceSessionTimeout).Should().BeEmpty();
        session.RemoteEndPoint.Should().Be(Endpoint());
    }

    [Fact]
    public void EvictExpiredVoiceBindings_IgnoresSessionsThatNeverBoundVoice()
    {
        _sessions.Add("conn-1", Guid.NewGuid(), "TextOnly");

        _time.Advance(TimeSpan.FromMinutes(5));

        _sessions.EvictExpiredVoiceBindings(ProtocolLimits.VoiceSessionTimeout).Should().BeEmpty();
    }

    [Fact]
    public void Remove_ClearsEveryLookupIndex()
    {
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "First");
        _sessions.TryBindEndpoint(session.VoiceToken, Endpoint(), out _);

        _sessions.Remove("conn-1").Should().BeSameAs(session);

        _sessions.TryGetByConnection("conn-1", out _).Should().BeFalse();
        _sessions.TryGetBySsrc(session.Ssrc, out _).Should().BeFalse();
        _sessions.TryBindEndpoint(session.VoiceToken, Endpoint(), out _).Should().BeFalse();
        _sessions.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_UnknownConnection_ReturnsNull()
    {
        _sessions.Remove("never-connected").Should().BeNull();
    }

    [Fact]
    public void InVoiceChannel_ReturnsOnlyThatChannelMembers()
    {
        var first = _sessions.Add("conn-1", Guid.NewGuid(), "First");
        var second = _sessions.Add("conn-2", Guid.NewGuid(), "Second");
        var third = _sessions.Add("conn-3", Guid.NewGuid(), "Third");
        first.VoiceChannelId = 7;
        second.VoiceChannelId = 7;
        third.VoiceChannelId = 8;

        _sessions.InVoiceChannel(7).Select(s => s.DisplayName).Should().BeEquivalentTo("First", "Second");
    }
}
