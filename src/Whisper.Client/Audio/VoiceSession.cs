using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Whisper.Client.Infrastructure;
using Whisper.Client.Models;
using Whisper.Shared;
using Whisper.Shared.Net;

namespace Whisper.Client.Audio;

public sealed class VoiceSession(
    Func<IUdpTransport> transportFactory,
    IAudioCapture capture,
    IAudioOutput output,
    Func<IAudioCodec> codecFactory,
    TimeProvider timeProvider,
    IAudioThread audioThread,
    ILogger<VoiceSession> logger) : IVoiceSession
{
    private static readonly TimeSpan HandshakeRetryInterval = TimeSpan.FromMilliseconds(250);
    private const int HandshakeAttempts = 8;

    /// <summary>
    /// Bounded and drop-oldest: if the network stalls, sending stale speech is worse than
    /// skipping it, and back-pressure onto the capture thread would glitch the device.
    /// </summary>
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly PeerMixer _mixer = new(codecFactory, timeProvider);
    private readonly VoiceActivityGate _gate = new(timeProvider);
    private readonly Lock _lifecycle = new();
    private readonly SemaphoreSlim _run = new(1, 1);

    private IAudioCodec? _encoder;
    private IUdpTransport? _transport;
    private IPEndPoint? _relay;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<uint>? _handshake;
    private Task? _receiveLoop;
    private Task? _sendLoop;
    private Task? _keepaliveLoop;
    private uint _ssrc;
    private ushort _sequence;
    private uint _timestamp;
    private bool _pushToTalkPressed;
    private string? _openedInputId;
    private string? _openedOutputId;

    public bool IsActive { get; private set; }

    public bool IsMuted { get; set; }

    public bool IsDeafened
    {
        get => _mixer.IsDeafened;
        set => _mixer.IsDeafened = value;
    }

    public bool IsTransmitting { get; private set; }

    public float InputLevel { get; private set; }

    public AudioSettings Settings { get; private set; } = new();

    public event EventHandler<float>? InputLevelChanged;

    public event EventHandler<Exception>? Failed;

    public async Task<bool> StartAsync(
        IPEndPoint relayEndPoint,
        Guid voiceToken,
        uint ssrc,
        CancellationToken cancellationToken = default)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await StopCoreAsync(releaseDevices: false).ConfigureAwait(false);
            return await StartCoreAsync(relayEndPoint, voiceToken, ssrc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.Release();
        }
    }

    private async Task<bool> StartCoreAsync(
        IPEndPoint relayEndPoint,
        Guid voiceToken,
        uint ssrc,
        CancellationToken cancellationToken)
    {
        _relay = relayEndPoint;
        _ssrc = ssrc;
        _sequence = 0;
        _timestamp = 0;
        _encoder = codecFactory();
        _gate.Reset();
        ApplySettings(Settings);

        var transport = transportFactory();
        transport.Bind(new IPEndPoint(IPAddress.Any, 0));
        _transport = transport;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation = cancellation;
        _handshake = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(transport, cancellation.Token), CancellationToken.None);
        _sendLoop = Task.Run(() => SendLoopAsync(transport, cancellation.Token), CancellationToken.None);

        if (!await CompleteHandshakeAsync(transport, voiceToken, cancellation.Token).ConfigureAwait(false))
        {
            logger.LogWarning("The voice relay at {Relay} did not answer the handshake.", relayEndPoint);
            await StopCoreAsync(releaseDevices: false).ConfigureAwait(false);
            return false;
        }

        _keepaliveLoop = Task.Run(() => KeepaliveLoopAsync(transport, cancellation.Token), CancellationToken.None);

        capture.FrameCaptured += OnFrameCaptured;
        capture.Failed += OnDeviceFailed;
        output.Failed += OnDeviceFailed;

        try
        {
            await EnsureDevicesAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open an audio device.");
            Failed?.Invoke(this, ex);
            await StopCoreAsync(releaseDevices: true).ConfigureAwait(false);
            return false;
        }

        IsActive = true;
        logger.LogInformation("Voice is live on {Relay} with ssrc {Ssrc}.", relayEndPoint, ssrc);
        return true;
    }

    public async Task StopAsync(bool releaseDevices = false)
    {
        await _run.WaitAsync().ConfigureAwait(false);

        try
        {
            await StopCoreAsync(releaseDevices).ConfigureAwait(false);
        }
        finally
        {
            _run.Release();
        }
    }

    private async Task StopCoreAsync(bool releaseDevices)
    {
        CancellationTokenSource? cancellation;
        IUdpTransport? transport;
        Task?[] loops;

        lock (_lifecycle)
        {
            cancellation = _cancellation;
            transport = _transport;
            loops = [_receiveLoop, _sendLoop, _keepaliveLoop];

            _cancellation = null;
            _transport = null;
            _receiveLoop = null;
            _sendLoop = null;
            _keepaliveLoop = null;
            IsActive = false;
            IsTransmitting = false;
        }

        capture.FrameCaptured -= OnFrameCaptured;
        capture.Failed -= OnDeviceFailed;
        output.Failed -= OnDeviceFailed;

        if (releaseDevices)
        {
            try
            {
                await audioThread.InvokeAsync(() =>
                {
                    capture.Stop();
                    output.Stop();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Audio device stop failed.");
            }

            _openedInputId = null;
            _openedOutputId = null;
        }

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        // Unblocks ReceiveFromAsync if cancellation alone did not, so the receive loop
        // cannot Enqueue into the mixer after we stop playback.
        transport?.Dispose();

        foreach (var loop in loops)
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "A voice loop did not exit cleanly.");
            }
        }

        cancellation?.Dispose();

        _mixer.Clear();
        _encoder?.Dispose();
        _encoder = null;

        while (_outbound.Reader.TryRead(out _))
        {
            // Drop anything still queued so a later session cannot transmit stale audio.
        }
    }

    /// <summary>
    /// WASAPI start/stop is the slow, hang-prone part of a call. Leave-voice keeps the
    /// devices warm so rejoining is only a UDP handshake.
    /// </summary>
    private async Task EnsureDevicesAsync(CancellationToken cancellationToken)
    {
        var inputId = Settings.InputDeviceId;
        var outputId = Settings.OutputDeviceId;
        var alreadyOpen = capture.IsCapturing
            && output.IsPlaying
            && string.Equals(_openedInputId, inputId, StringComparison.Ordinal)
            && string.Equals(_openedOutputId, outputId, StringComparison.Ordinal);

        if (alreadyOpen)
        {
            return;
        }

        await audioThread.InvokeAsync(() =>
        {
            output.Start(outputId, _mixer);
            capture.Start(inputId);
        }, cancellationToken).ConfigureAwait(false);

        _openedInputId = inputId;
        _openedOutputId = outputId;
    }

    public void ApplySettings(AudioSettings settings)
    {
        Settings = settings;
        _gate.ThresholdDb = settings.VoiceActivityThresholdDb;
        _mixer.JitterBufferFrames = Math.Clamp(settings.JitterBufferFrames, 1, 10);

        if (capture is NAudioCapture nAudioCapture)
        {
            nAudioCapture.Gain = settings.InputGain;
        }
    }

    public void SetPushToTalkPressed(bool isPressed) => _pushToTalkPressed = isPressed;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(releaseDevices: true).ConfigureAwait(false);

        try
        {
            await audioThread.InvokeAsync(() =>
            {
                capture.Dispose();
                output.Dispose();
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            capture.Dispose();
            output.Dispose();
        }

        _mixer.Dispose();
        _run.Dispose();
    }

    private async Task<bool> CompleteHandshakeAsync(
        IUdpTransport transport,
        Guid voiceToken,
        CancellationToken cancellationToken)
    {
        var handshake = new byte[VoicePacket.HandshakeSize];
        VoicePacket.WriteHandshake(handshake, voiceToken);

        for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
        {
            try
            {
                await transport.SendToAsync(handshake, _relay!, cancellationToken).ConfigureAwait(false);

                var completed = await Task.WhenAny(
                    _handshake!.Task,
                    Task.Delay(HandshakeRetryInterval, cancellationToken)).ConfigureAwait(false);

                if (completed == _handshake.Task)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Voice handshake attempt {Attempt} failed.", attempt + 1);
            }
        }

        return false;
    }

    private async Task ReceiveLoopAsync(IUdpTransport transport, CancellationToken cancellationToken)
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
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ignoring a voice receive error.");
                continue;
            }

            var datagramSpan = buffer.AsSpan(0, datagram.BytesReceived);

            // Only the relay's endpoint is trusted: anything else on this socket is noise
            // or someone trying to inject audio.
            if (!datagram.RemoteEndPoint.Equals(_relay))
            {
                continue;
            }

            if (!VoicePacket.TryPeekType(datagramSpan, out var type))
            {
                continue;
            }

            switch (type)
            {
                case VoiceMessageType.HandshakeAck when VoicePacket.TryReadHandshakeAck(datagramSpan, out var ackSsrc):
                    _handshake?.TrySetResult(ackSsrc);
                    break;

                case VoiceMessageType.Audio when VoicePacket.TryReadAudio(datagramSpan, out var header, out var payload):
                    if (header.Ssrc != _ssrc)
                    {
                        _mixer.Enqueue(header.Ssrc, header.Sequence, payload);
                    }

                    break;
            }
        }
    }

    private async Task SendLoopAsync(IUdpTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var datagram in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await transport.SendToAsync(datagram, _relay!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The voice send loop stopped.");
        }
    }

    private async Task KeepaliveLoopAsync(IUdpTransport transport, CancellationToken cancellationToken)
    {
        var keepalive = new byte[VoicePacket.KeepaliveSize];
        VoicePacket.WriteKeepalive(keepalive, _ssrc);

        using var timer = new PeriodicTimer(ProtocolLimits.VoiceKeepaliveInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Holds the NAT mapping and the server-side session open through silence.
                await transport.SendToAsync(keepalive, _relay!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The voice keepalive loop stopped.");
        }
    }

    private void OnFrameCaptured(object? sender, short[] frame)
    {
        var encoder = _encoder;

        if (encoder is null)
        {
            return;
        }

        var open = _gate.Process(frame);
        InputLevel = _gate.LastLevel;
        InputLevelChanged?.Invoke(this, InputLevel);

        var wantsToTalk = Settings.UsePushToTalk ? _pushToTalkPressed : open;
        var transmitting = wantsToTalk && !IsMuted;
        IsTransmitting = transmitting;

        if (!transmitting)
        {
            return;
        }

        try
        {
            var packet = new byte[VoicePacket.MaxPacketSize];
            var payload = new byte[AudioFormat.MaxOpusPayload];
            var encodedLength = encoder.Encode(frame, payload);

            var length = VoicePacket.WriteAudio(
                packet,
                _ssrc,
                _sequence,
                _timestamp,
                payload.AsSpan(0, encodedLength));

            _sequence = SequenceNumber.Next(_sequence);
            _timestamp += AudioFormat.FrameSamples;

            _outbound.Writer.TryWrite(packet[..length]);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Dropped a captured frame.");
        }
    }

    private void OnDeviceFailed(object? sender, Exception exception)
    {
        logger.LogError(exception, "An audio device failed.");
        Failed?.Invoke(this, exception);
    }
}
