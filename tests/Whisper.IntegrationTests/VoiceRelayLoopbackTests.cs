using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Whisper.Server;
using Whisper.Server.Voice;
using Whisper.Shared;
using Whisper.Shared.Net;
using Xunit;

namespace Whisper.IntegrationTests;

/// <summary>
/// Drives the relay over a real UDP socket on an ephemeral loopback port. This is the only
/// place the actual socket path is covered: the routing rules themselves are unit tested.
/// </summary>
[Trait("Category", "Integration")]
public class VoiceRelayLoopbackTests : IAsyncLifetime
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionRegistry _sessions = new(TimeProvider.System);
    private readonly UdpSocketTransport _transport = new();
    private readonly IVoiceNotifier _notifier = Substitute.For<IVoiceNotifier>();
    private UdpVoiceRelay _relay = null!;
    private IPEndPoint _relayEndPoint = null!;

    public async Task InitializeAsync()
    {
        _relay = new UdpVoiceRelay(
            _transport,
            _sessions,
            new VoiceRouter(_sessions),
            new SpeakingTracker(TimeProvider.System),
            _notifier,
            Options.Create(new WhisperServerOptions()),
            TimeProvider.System,
            NullLogger<UdpVoiceRelay>.Instance);

        _relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _relayEndPoint = _relay.BoundEndPoint!;
        await _relay.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _relay.StopAsync(CancellationToken.None);
        _transport.Dispose();
    }

    [Fact]
    public async Task Handshake_OverALoopbackSocket_IsAcknowledgedWithTheAssignedSsrc()
    {
        using var client = new TestVoiceClient(_relayEndPoint);
        var session = _sessions.Add("conn-1", Guid.NewGuid(), "Tom");

        var ssrc = await client.HandshakeAsync(session.VoiceToken);

        ssrc.Should().Be(session.Ssrc);
        session.RemoteEndPoint.Should().NotBeNull();
    }

    [Fact]
    public async Task Audio_ReachesPeersInTheSameChannelAndNotTheOtherChannel()
    {
        using var speaker = new TestVoiceClient(_relayEndPoint);
        using var sameChannelA = new TestVoiceClient(_relayEndPoint);
        using var sameChannelB = new TestVoiceClient(_relayEndPoint);
        using var otherChannel = new TestVoiceClient(_relayEndPoint);

        var speakerSsrc = await JoinAsync(speaker, "speaker", channelId: 1);
        await JoinAsync(sameChannelA, "listenerA", channelId: 1);
        await JoinAsync(sameChannelB, "listenerB", channelId: 1);
        await JoinAsync(otherChannel, "elsewhere", channelId: 2);

        var packet = BuildAudio(speakerSsrc, sequence: 1);
        await speaker.SendAsync(packet);

        (await sameChannelA.ReceiveAsync()).Should().Equal(packet);
        (await sameChannelB.ReceiveAsync()).Should().Equal(packet);

        var elsewhere = async () => await otherChannel.ReceiveAsync(TimeSpan.FromMilliseconds(400));
        await elsewhere.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Audio_SentBeforeTheHandshake_IsIgnored()
    {
        using var speaker = new TestVoiceClient(_relayEndPoint);
        using var listener = new TestVoiceClient(_relayEndPoint);

        var unbound = _sessions.Add("conn-unbound", Guid.NewGuid(), "Impatient");
        unbound.VoiceChannelId = 1;
        await JoinAsync(listener, "listener", channelId: 1);

        await speaker.SendAsync(BuildAudio(unbound.Ssrc, sequence: 1));

        var nothing = async () => await listener.ReceiveAsync(TimeSpan.FromMilliseconds(400));
        await nothing.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Audio_FromAMemberWhoLeftVoice_StopsBeingForwarded()
    {
        using var speaker = new TestVoiceClient(_relayEndPoint);
        using var listener = new TestVoiceClient(_relayEndPoint);

        var speakerSsrc = await JoinAsync(speaker, "speaker", channelId: 1);
        await JoinAsync(listener, "listener", channelId: 1);

        await speaker.SendAsync(BuildAudio(speakerSsrc, sequence: 1));
        (await listener.ReceiveAsync()).Should().NotBeEmpty();

        _sessions.TryGetBySsrc(speakerSsrc, out var session).Should().BeTrue();
        session.VoiceChannelId = null;

        await speaker.SendAsync(BuildAudio(speakerSsrc, sequence: 2));

        var nothing = async () => await listener.ReceiveAsync(TimeSpan.FromMilliseconds(400));
        await nothing.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<uint> JoinAsync(TestVoiceClient client, string name, int channelId)
    {
        var session = _sessions.Add($"conn-{name}", Guid.NewGuid(), name);
        var ssrc = await client.HandshakeAsync(session.VoiceToken);
        session.VoiceChannelId = channelId;
        return ssrc;
    }

    private static byte[] BuildAudio(uint ssrc, ushort sequence)
    {
        var payload = new byte[60];
        Array.Fill(payload, (byte)sequence);

        var buffer = new byte[VoicePacket.AudioHeaderSize + payload.Length];
        VoicePacket.WriteAudio(buffer, ssrc, sequence, sequence * (uint)AudioFormat.FrameSamples, payload);
        return buffer;
    }

    private sealed class TestVoiceClient : IDisposable
    {
        private readonly UdpClient _client = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly IPEndPoint _relay;

        public TestVoiceClient(IPEndPoint relay) => _relay = relay;

        public async Task<uint> HandshakeAsync(Guid voiceToken)
        {
            var buffer = new byte[VoicePacket.HandshakeSize];
            VoicePacket.WriteHandshake(buffer, voiceToken);
            await SendAsync(buffer);

            var ack = await ReceiveAsync();
            VoicePacket.TryReadHandshakeAck(ack, out var ssrc).Should().BeTrue();
            return ssrc;
        }

        public async Task SendAsync(byte[] datagram) =>
            await _client.SendAsync(datagram, _relay);

        public async Task<byte[]> ReceiveAsync(TimeSpan? timeout = null)
        {
            using var cts = new CancellationTokenSource(timeout ?? ReceiveTimeout);
            var result = await _client.ReceiveAsync(cts.Token);
            return result.Buffer;
        }

        public void Dispose() => _client.Dispose();
    }
}
