using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Whisper.Server.Voice;
using Xunit;

namespace Whisper.Tests.Server;

public class VoiceRouterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionRegistry _sessions;
    private readonly VoiceRouter _router;
    private readonly List<IPEndPoint> _destinations = [];

    public VoiceRouterTests()
    {
        _sessions = new SessionRegistry(_time);
        _router = new VoiceRouter(_sessions);
    }

    private VoiceSession AddMember(string name, int port, int? voiceChannelId = 1, bool bind = true)
    {
        var session = _sessions.Add($"conn-{name}", Guid.NewGuid(), name);

        if (bind)
        {
            _sessions.TryBindEndpoint(session.VoiceToken, Endpoint(port), out _).Should().BeTrue();
        }

        session.VoiceChannelId = voiceChannelId;
        return session;
    }

    private static IPEndPoint Endpoint(int port) => new(IPAddress.Parse("192.168.1.10"), port);

    [Fact]
    public void Route_ForwardsToEveryOtherMemberOfTheSenderChannel()
    {
        var sender = AddMember("sender", 5000);
        AddMember("listenerA", 5001);
        AddMember("listenerB", 5002);

        var outcome = _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out var resolved);

        outcome.Should().Be(RouteOutcome.Forwarded);
        resolved.Should().BeSameAs(sender);
        _destinations.Should().BeEquivalentTo([Endpoint(5001), Endpoint(5002)]);
    }

    [Fact]
    public void Route_NeverEchoesBackToTheSender()
    {
        var sender = AddMember("sender", 5000);
        AddMember("listener", 5001);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _);

        _destinations.Should().NotContain(Endpoint(5000));
    }

    [Fact]
    public void Route_ExcludesMembersInADifferentVoiceChannel()
    {
        var sender = AddMember("sender", 5000, voiceChannelId: 1);
        AddMember("sameChannel", 5001, voiceChannelId: 1);
        AddMember("otherChannel", 5002, voiceChannelId: 2);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.Forwarded);

        _destinations.Should().Equal([Endpoint(5001)]);
    }

    [Fact]
    public void Route_ExcludesMembersNotInAnyVoiceChannel()
    {
        var sender = AddMember("sender", 5000);
        AddMember("textOnly", 5001, voiceChannelId: null);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.NoRecipients);

        _destinations.Should().BeEmpty();
    }

    [Fact]
    public void Route_ExcludesDeafenedMembers()
    {
        var sender = AddMember("sender", 5000);
        var deafened = AddMember("deafened", 5001);
        deafened.IsDeafened = true;

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.NoRecipients);
    }

    [Fact]
    public void Route_ExcludesMembersThatNeverCompletedTheUdpHandshake()
    {
        var sender = AddMember("sender", 5000);
        AddMember("noUdp", 5001, bind: false);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.NoRecipients);
    }

    [Fact]
    public void Route_UnknownSsrc_IsRejected()
    {
        AddMember("sender", 5000);

        _router.Route(ssrc: 999_999, Endpoint(5000), _destinations, out var resolved)
            .Should().Be(RouteOutcome.UnknownSsrc);

        resolved.Should().BeNull();
        _destinations.Should().BeEmpty();
    }

    [Fact]
    public void Route_SenderWithoutABinding_IsRejected()
    {
        var sender = AddMember("sender", 5000, bind: false);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.NotBound);
    }

    [Fact]
    public void Route_PacketFromAnEndpointOtherThanTheBoundOne_IsRejected()
    {
        // Someone on the network claiming another member's ssrc must not get through.
        var sender = AddMember("sender", 5000);
        AddMember("listener", 5001);

        _router.Route(sender.Ssrc, new IPEndPoint(IPAddress.Parse("10.0.0.9"), 5000), _destinations, out _)
            .Should().Be(RouteOutcome.EndpointMismatch);

        _destinations.Should().BeEmpty();
    }

    [Fact]
    public void Route_SenderNotInAVoiceChannel_IsRejected()
    {
        var sender = AddMember("sender", 5000, voiceChannelId: null);
        AddMember("listener", 5001);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.NotInVoiceChannel);
    }

    [Fact]
    public void Route_MutedSender_IsDroppedAtTheServer()
    {
        var sender = AddMember("sender", 5000);
        sender.IsMuted = true;
        AddMember("listener", 5001);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.SenderMuted);

        _destinations.Should().BeEmpty();
    }

    [Fact]
    public void Route_AloneInTheChannel_ReportsNoRecipients()
    {
        var sender = AddMember("sender", 5000);

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _)
            .Should().Be(RouteOutcome.NoRecipients);
    }

    [Fact]
    public void Route_ClearsDestinationsLeftBehindByThePreviousPacket()
    {
        var sender = AddMember("sender", 5000);
        _destinations.Add(Endpoint(9999));

        _router.Route(sender.Ssrc, Endpoint(5000), _destinations, out _);

        _destinations.Should().NotContain(Endpoint(9999));
    }
}
