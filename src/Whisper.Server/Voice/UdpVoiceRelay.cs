using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Whisper.Server.RateLimiting;
using Whisper.Shared;
using Whisper.Shared.Net;

namespace Whisper.Server.Voice;

/// <summary>
/// The media plane. A single receive loop validates each datagram, then forwards audio
/// byte-for-byte to the other members of the sender's voice channel. Nothing is decoded
/// or re-encoded here: that is what keeps a self-hosted server cheap to run.
/// </summary>
public sealed class UdpVoiceRelay(
    IUdpTransport transport,
    SessionRegistry sessions,
    VoiceRouter router,
    SpeakingTracker speaking,
    IVoiceNotifier notifier,
    IOptions<WhisperServerOptions> options,
    TimeProvider timeProvider,
    ILogger<UdpVoiceRelay> logger) : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMilliseconds(100);

    private readonly WhisperServerOptions _options = options.Value;
    private readonly List<IPEndPoint> _destinations = [];
    private readonly byte[] _ackBuffer = new byte[VoicePacket.HandshakeAckSize];

    // Handshakes are the only unauthenticated entry point on this socket, so they get
    // their own budget per source address to blunt token guessing.
    private readonly TokenBucketRateLimiter _handshakeLimiter =
        new(timeProvider, burst: 10, window: TimeSpan.FromSeconds(5));

    // A bound session is trusted to send roughly 50 packets a second. Anything far beyond
    // that is either broken or an attempt to use the relay as an amplifier against the
    // rest of the channel, since every packet is fanned out to every other member.
    private readonly TokenBucketRateLimiter _audioLimiter = new(
        timeProvider,
        burst: options.Value.VoicePacketsPerSecond,
        window: TimeSpan.FromSeconds(1));

    public IPEndPoint? BoundEndPoint => transport.LocalEndPoint;

    public bool IsListening { get; private set; }

    /// <summary>Datagrams dropped as malformed, unauthenticated, or spoofed.</summary>
    public long DroppedPackets { get; private set; }

    public long ForwardedPackets { get; private set; }

    /// <summary>Audio datagrams dropped for exceeding a session's packet budget.</summary>
    public long ThrottledPackets { get; private set; }

    public void Bind(IPEndPoint endPoint)
    {
        transport.Bind(endPoint);
        IsListening = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsListening)
        {
            try
            {
                Bind(new IPEndPoint(IPAddress.Any, _options.VoicePort));
            }
            catch (SocketException ex)
            {
                // Chat is still perfectly usable without voice, so a busy port must not
                // take the whole server down with it.
                logger.LogError(
                    ex,
                    "Could not bind the voice relay to UDP {Port}. Voice is disabled; chat is unaffected.",
                    _options.VoicePort);
                return;
            }
        }

        logger.LogInformation("Voice relay listening on {EndPoint}.", BoundEndPoint);

        await Task.WhenAll(
            ReceiveLoopAsync(stoppingToken),
            MaintenanceLoopAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[VoicePacket.MaxPacketSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpDatagram datagram;

            try
            {
                datagram = await transport.ReceiveFromAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Ignoring a socket error on the voice relay.");
                continue;
            }

            await HandleDatagramAsync(
                new ReadOnlyMemory<byte>(buffer, 0, datagram.BytesReceived),
                datagram.RemoteEndPoint,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes a single datagram. Public so the relay's validation and forwarding rules
    /// can be driven directly from tests through a fake transport.
    /// </summary>
    public async ValueTask HandleDatagramAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint source,
        CancellationToken cancellationToken = default)
    {
        if (!VoicePacket.TryPeekType(datagram.Span, out var type))
        {
            DroppedPackets++;
            return;
        }

        switch (type)
        {
            case VoiceMessageType.Handshake:
                await HandleHandshakeAsync(datagram, source, cancellationToken).ConfigureAwait(false);
                break;

            case VoiceMessageType.Audio:
                await HandleAudioAsync(datagram, source, cancellationToken).ConfigureAwait(false);
                break;

            case VoiceMessageType.Keepalive:
                HandleKeepalive(datagram, source);
                break;

            default:
                // HandshakeAck only ever travels server to client.
                DroppedPackets++;
                break;
        }
    }

    private async ValueTask HandleHandshakeAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint source,
        CancellationToken cancellationToken)
    {
        if (!_handshakeLimiter.TryAcquire(source.Address.ToString()))
        {
            DroppedPackets++;
            logger.LogWarning("Throttled voice handshakes from {Source}.", source.Address);
            return;
        }

        if (!VoicePacket.TryReadHandshake(datagram.Span, out var voiceToken)
            || !sessions.TryBindEndpoint(voiceToken, source, out var session))
        {
            DroppedPackets++;
            logger.LogDebug("Rejected a voice handshake from {Source}.", source);
            return;
        }

        var length = VoicePacket.WriteHandshakeAck(_ackBuffer, session.Ssrc);
        await transport
            .SendToAsync(new ReadOnlyMemory<byte>(_ackBuffer, 0, length), source, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "{DisplayName} bound voice to {Source} (ssrc {Ssrc}).",
            session.DisplayName,
            source,
            session.Ssrc);
    }

    private async ValueTask HandleAudioAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint source,
        CancellationToken cancellationToken)
    {
        if (!VoicePacket.TryReadAudio(datagram.Span, out var header, out _))
        {
            DroppedPackets++;
            return;
        }

        var outcome = router.Route(header.Ssrc, source, _destinations, out var sender);

        if (sender is not null)
        {
            sessions.Touch(sender);
        }

        // Charged only once the packet is known to come from the endpoint bound to that
        // ssrc, so a spoofer cannot burn through someone else's budget.
        if (outcome is RouteOutcome.Forwarded or RouteOutcome.NoRecipients
            && !_audioLimiter.TryAcquire(header.Ssrc.ToString()))
        {
            ThrottledPackets++;
            DroppedPackets++;
            return;
        }

        switch (outcome)
        {
            case RouteOutcome.Forwarded:
                if (speaking.NotePacket(header.Ssrc))
                {
                    await notifier.SpeakingChanged(header.Ssrc, true).ConfigureAwait(false);
                }

                foreach (var destination in _destinations)
                {
                    await transport.SendToAsync(datagram, destination, cancellationToken).ConfigureAwait(false);
                }

                ForwardedPackets++;
                break;

            case RouteOutcome.NoRecipients:
                // Talking to an empty channel is normal; still show the speaking indicator.
                if (speaking.NotePacket(header.Ssrc))
                {
                    await notifier.SpeakingChanged(header.Ssrc, true).ConfigureAwait(false);
                }

                break;

            default:
                DroppedPackets++;
                logger.LogDebug("Dropped audio from {Source}: {Outcome}.", source, outcome);
                break;
        }
    }

    private void HandleKeepalive(ReadOnlyMemory<byte> datagram, IPEndPoint source)
    {
        if (!VoicePacket.TryReadKeepalive(datagram.Span, out var ssrc)
            || !sessions.TryGetBySsrc(ssrc, out var session)
            || session.RemoteEndPoint is not { } bound
            || !bound.Equals(source))
        {
            DroppedPackets++;
            return;
        }

        sessions.Touch(session);
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);
        var ticksUntilEviction = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunMaintenanceAsync(evictExpired: ticksUntilEviction-- <= 0).ConfigureAwait(false);

                if (ticksUntilEviction < 0)
                {
                    ticksUntilEviction = 9; // roughly once a second
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Exposed for tests so decay and eviction can be driven without waiting on a timer.</summary>
    public async Task RunMaintenanceAsync(bool evictExpired = true)
    {
        foreach (var ssrc in speaking.CollectStopped(ProtocolLimits.SpeakingDecay))
        {
            await notifier.SpeakingChanged(ssrc, false).ConfigureAwait(false);
        }

        if (!evictExpired)
        {
            return;
        }

        foreach (var session in sessions.EvictExpiredVoiceBindings(ProtocolLimits.VoiceSessionTimeout))
        {
            logger.LogInformation(
                "Voice binding for {DisplayName} timed out; they stopped sending keepalives.",
                session.DisplayName);

            speaking.Forget(session.Ssrc);
            _audioLimiter.Forget(session.Ssrc.ToString());
            await notifier.MemberUpdated(session).ConfigureAwait(false);
        }
    }
}
