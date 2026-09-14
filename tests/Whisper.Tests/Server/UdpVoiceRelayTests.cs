using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Whisper.Server;
using Whisper.Server.Voice;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Server;

public class UdpVoiceRelayTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeUdpTransport _transport = new();
    private readonly IVoiceNotifier _notifier = Substitute.For<IVoiceNotifier>();
    private readonly SessionRegistry _sessions;
    private readonly SpeakingTracker _speaking;
    private readonly UdpVoiceRelay _relay;

    public UdpVoiceRelayTests()
    {
        _sessions = new SessionRegistry(_time);
        _speaking = new SpeakingTracker(_time);

        _relay = new UdpVoiceRelay(
            _transport,
            _sessions,
            new VoiceRouter(_sessions),
            _speaking,
            _notifier,
            Options.Create(new WhisperServerOptions()),
            _time,
            NullLogger<UdpVoiceRelay>.Instance);
    }

    private static IPEndPoint Endpoint(int port) => new(IPAddress.Parse("192.168.1.20"), port);

    private static byte[] Handshake(Guid token)
    {
        var buffer = new byte[VoicePacket.HandshakeSize];
        VoicePacket.WriteHandshake(buffer, token);
        return buffer;
    }

    private static byte[] Audio(uint ssrc, ushort sequence = 1, byte fill = 0xAB)
    {
        var payload = new byte[80];
        Array.Fill(payload, fill);

        var buffer = new byte[VoicePacket.AudioHeaderSize + payload.Length];
        VoicePacket.WriteAudio(buffer, ssrc, sequence, sequence * (uint)AudioFormat.FrameSamples, payload);
        return buffer;
    }

    private static byte[] Keepalive(uint ssrc)
    {
        var buffer = new byte[VoicePacket.KeepaliveSize];
        VoicePacket.WriteKeepalive(buffer, ssrc);
        return buffer;
    }

    private async Task<VoiceSession> JoinVoiceAsync(string name, int port, int channelId = 1)
    {
        var session = _sessions.Add($"conn-{name}", Guid.NewGuid(), name);
        await _relay.HandleDatagramAsync(Handshake(session.VoiceToken), Endpoint(port));
        session.VoiceChannelId = channelId;
        return session;
    }

    [Fact]
    public async Task Handshake_WithAValidToken_BindsTheEndpointAndAcknowledges()
    {
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "Tom");

        await _relay.HandleDatagramAsync(Handshake(session.VoiceToken), Endpoint(5000));

        session.RemoteEndPoint.Should().Be(Endpoint(5000));
        _transport.Sent.Should().ContainSingle();

        var (payload, destination) = _transport.Sent[0];
        destination.Should().Be(Endpoint(5000));
        VoicePacket.TryReadHandshakeAck(payload, out var ackSsrc).Should().BeTrue();
        ackSsrc.Should().Be(session.Ssrc);
    }

    [Fact]
    public async Task Handshake_WithAnUnknownToken_IsIgnoredWithoutReply()
    {
        await _relay.HandleDatagramAsync(Handshake(Guid.NewGuid()), Endpoint(5000));

        _transport.Sent.Should().BeEmpty();
        _relay.DroppedPackets.Should().Be(1);
    }

    [Fact]
    public async Task Handshake_RepeatedFromOneAddress_IsThrottled()
    {
        // Token guessing over UDP gets one budget per source address.
        for (var i = 0; i < 40; i++)
        {
            await _relay.HandleDatagramAsync(Handshake(Guid.NewGuid()), Endpoint(5000));
        }

        var session = _sessions.Add("conn-late", Guid.NewGuid(), "Late");
        await _relay.HandleDatagramAsync(Handshake(session.VoiceToken), Endpoint(5000));

        session.RemoteEndPoint.Should().BeNull("the address had already exhausted its handshake budget");

        _time.Advance(TimeSpan.FromSeconds(10));
        await _relay.HandleDatagramAsync(Handshake(session.VoiceToken), Endpoint(5000));

        session.RemoteEndPoint.Should().Be(Endpoint(5000), "the bucket refills over time");
    }

    [Fact]
    public async Task Audio_FromABoundSender_IsForwardedByteForByteToChannelPeers()
    {
        var sender = await JoinVoiceAsync("sender", 5000);
        await JoinVoiceAsync("listener", 5001);
        _transport.Sent.Clear();

        var packet = Audio(sender.Ssrc);
        await _relay.HandleDatagramAsync(packet, Endpoint(5000));

        _transport.Sent.Should().ContainSingle();
        _transport.Sent[0].Destination.Should().Be(Endpoint(5001));
        _transport.Sent[0].Payload.Should().Equal(packet);
        _relay.ForwardedPackets.Should().Be(1);
    }

    [Fact]
    public async Task Audio_ReachesEveryPeerInTheChannelButNobodyElse()
    {
        var sender = await JoinVoiceAsync("sender", 5000, channelId: 1);
        await JoinVoiceAsync("sameChannelA", 5001, channelId: 1);
        await JoinVoiceAsync("sameChannelB", 5002, channelId: 1);
        await JoinVoiceAsync("otherChannel", 5003, channelId: 2);
        _transport.Sent.Clear();

        await _relay.HandleDatagramAsync(Audio(sender.Ssrc), Endpoint(5000));

        _transport.Sent.Select(s => s.Destination)
            .Should().BeEquivalentTo([Endpoint(5001), Endpoint(5002)]);
    }

    [Fact]
    public async Task Audio_BeforeAnyHandshake_IsDropped()
    {
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "Impatient");
        session.VoiceChannelId = 1;
        await JoinVoiceAsync("listener", 5001);
        _transport.Sent.Clear();

        await _relay.HandleDatagramAsync(Audio(session.Ssrc), Endpoint(5000));

        _transport.Sent.Should().BeEmpty();
        _relay.DroppedPackets.Should().Be(1);
    }

    [Fact]
    public async Task Audio_ClaimingAnotherMemberSsrcFromADifferentAddress_IsDropped()
    {
        var victim = await JoinVoiceAsync("victim", 5000);
        await JoinVoiceAsync("listener", 5001);
        _transport.Sent.Clear();

        await _relay.HandleDatagramAsync(Audio(victim.Ssrc), new IPEndPoint(IPAddress.Parse("10.9.9.9"), 5000));

        _transport.Sent.Should().BeEmpty();
        _relay.ForwardedPackets.Should().Be(0);
    }

    [Fact]
    public async Task Audio_FromAnUnknownSsrc_IsDropped()
    {
        await JoinVoiceAsync("listener", 5001);
        _transport.Sent.Clear();

        await _relay.HandleDatagramAsync(Audio(ssrc: 123456), Endpoint(5000));

        _transport.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Audio_RaisesSpeakingChangedOnceForAContinuousBurst()
    {
        var sender = await JoinVoiceAsync("sender", 5000);
        await JoinVoiceAsync("listener", 5001);

        for (ushort sequence = 1; sequence <= 10; sequence++)
        {
            await _relay.HandleDatagramAsync(Audio(sender.Ssrc, sequence), Endpoint(5000));
            _time.Advance(TimeSpan.FromMilliseconds(20));
        }

        await _notifier.Received(1).SpeakingChanged(sender.Ssrc, true);
        await _notifier.DidNotReceive().SpeakingChanged(sender.Ssrc, false);
    }

    [Fact]
    public async Task Maintenance_AfterTheSpeakerFallsSilent_RaisesSpeakingChangedFalse()
    {
        var sender = await JoinVoiceAsync("sender", 5000);
        await _relay.HandleDatagramAsync(Audio(sender.Ssrc), Endpoint(5000));

        _time.Advance(ProtocolLimits.SpeakingDecay * 2);
        await _relay.RunMaintenanceAsync();

        await _notifier.Received(1).SpeakingChanged(sender.Ssrc, false);
    }

    [Fact]
    public async Task Maintenance_AfterTheKeepaliveWindow_EvictsTheBindingAndNotifies()
    {
        var sender = await JoinVoiceAsync("sender", 5000);

        _time.Advance(ProtocolLimits.VoiceSessionTimeout + TimeSpan.FromSeconds(1));
        await _relay.RunMaintenanceAsync();

        sender.RemoteEndPoint.Should().BeNull();
        sender.VoiceChannelId.Should().BeNull();
        await _notifier.Received(1).MemberUpdated(sender);
    }

    [Fact]
    public async Task Keepalive_KeepsTheBindingAliveAcrossTheTimeoutWindow()
    {
        var sender = await JoinVoiceAsync("sender", 5000);

        for (var i = 0; i < 6; i++)
        {
            _time.Advance(ProtocolLimits.VoiceKeepaliveInterval);
            await _relay.HandleDatagramAsync(Keepalive(sender.Ssrc), Endpoint(5000));
        }

        await _relay.RunMaintenanceAsync();

        sender.RemoteEndPoint.Should().Be(Endpoint(5000));
        await _notifier.DidNotReceive().MemberUpdated(Arg.Any<VoiceSession>());
    }

    [Fact]
    public async Task Keepalive_FromTheWrongAddress_IsIgnored()
    {
        var sender = await JoinVoiceAsync("sender", 5000);

        _time.Advance(ProtocolLimits.VoiceSessionTimeout - TimeSpan.FromSeconds(1));
        await _relay.HandleDatagramAsync(Keepalive(sender.Ssrc), new IPEndPoint(IPAddress.Parse("10.9.9.9"), 5000));
        _time.Advance(TimeSpan.FromSeconds(2));
        await _relay.RunMaintenanceAsync();

        sender.RemoteEndPoint.Should().BeNull("a keepalive from elsewhere must not refresh the session");
    }

    [Fact]
    public async Task UnknownMessageType_IsDroppedWithoutReply()
    {
        await _relay.HandleDatagramAsync(new byte[] { 0x7F, 0x00, 0x00 }, Endpoint(5000));

        _transport.Sent.Should().BeEmpty();
        _relay.DroppedPackets.Should().Be(1);
    }

    [Fact]
    public async Task EmptyDatagram_IsDroppedWithoutThrowing()
    {
        await _relay.HandleDatagramAsync(ReadOnlyMemory<byte>.Empty, Endpoint(5000));

        _relay.DroppedPackets.Should().Be(1);
    }

    [Fact]
    public async Task TruncatedAudioPacket_IsDropped()
    {
        var sender = await JoinVoiceAsync("sender", 5000);
        await JoinVoiceAsync("listener", 5001);
        _transport.Sent.Clear();

        var truncated = Audio(sender.Ssrc).AsMemory(0, VoicePacket.AudioHeaderSize + 4);
        await _relay.HandleDatagramAsync(truncated, Endpoint(5000));

        _transport.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Audio_FromAMutedSender_IsDroppedAtTheServer()
    {
        var sender = await JoinVoiceAsync("sender", 5000);
        await JoinVoiceAsync("listener", 5001);
        sender.IsMuted = true;
        _transport.Sent.Clear();

        await _relay.HandleDatagramAsync(Audio(sender.Ssrc), Endpoint(5000));

        _transport.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Audio_FloodedFasterThanRealTime_IsThrottled()
    {
        // Every forwarded packet is fanned out to the whole channel, so an unthrottled
        // flood would turn the relay into an amplifier.
        var sender = await JoinVoiceAsync("sender", 5000);
        await JoinVoiceAsync("listener", 5001);
        _transport.Sent.Clear();

        for (var i = 0; i < 400; i++)
        {
            await _relay.HandleDatagramAsync(Audio(sender.Ssrc, (ushort)i), Endpoint(5000));
        }

        var budget = new WhisperServerOptions().VoicePacketsPerSecond;
        _relay.ForwardedPackets.Should().Be(budget);
        _relay.ThrottledPackets.Should().Be(400 - budget);
    }

    [Fact]
    public async Task Audio_AtTheNormalFrameRate_IsNeverThrottled()
    {
        // 50 packets a second is exactly what a well-behaved client sends.
        var sender = await JoinVoiceAsync("sender", 5000);
        await JoinVoiceAsync("listener", 5001);

        for (var i = 0; i < 500; i++)
        {
            await _relay.HandleDatagramAsync(Audio(sender.Ssrc, (ushort)i), Endpoint(5000));
            _time.Advance(TimeSpan.FromMilliseconds(AudioFormat.FrameMilliseconds));
        }

        _relay.ThrottledPackets.Should().Be(0);
        _relay.ForwardedPackets.Should().Be(500);
    }

    [Fact]
    public async Task Audio_FloodedByOneSender_DoesNotSpendAnotherSendersBudget()
    {
        var flooder = await JoinVoiceAsync("flooder", 5000);
        var quiet = await JoinVoiceAsync("quiet", 5001);
        await JoinVoiceAsync("listener", 5002);

        for (var i = 0; i < 400; i++)
        {
            await _relay.HandleDatagramAsync(Audio(flooder.Ssrc, (ushort)i), Endpoint(5000));
        }

        _transport.Sent.Clear();
        await _relay.HandleDatagramAsync(Audio(quiet.Ssrc), Endpoint(5001));

        _transport.Sent.Should().NotBeEmpty("budgets are per session");
    }

    [Fact]
    public async Task Audio_SpoofedWithAnotherSsrc_DoesNotSpendThatSessionsBudget()
    {
        var victim = await JoinVoiceAsync("victim", 5000);
        await JoinVoiceAsync("listener", 5001);
        var attacker = new IPEndPoint(IPAddress.Parse("10.9.9.9"), 6000);

        for (var i = 0; i < 400; i++)
        {
            await _relay.HandleDatagramAsync(Audio(victim.Ssrc, (ushort)i), attacker);
        }

        _transport.Sent.Clear();
        await _relay.HandleDatagramAsync(Audio(victim.Ssrc), Endpoint(5000));

        _transport.Sent.Should().NotBeEmpty("the spoofed packets were rejected before being charged");
        _relay.ThrottledPackets.Should().Be(0);
    }
}
