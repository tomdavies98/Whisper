using System.Net;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.Client.Audio;
using Whisper.Client.Models;
using Whisper.Shared;
using Whisper.Shared.Net;
using Xunit;

namespace Whisper.Tests.Client;

public class VoiceSessionTests : IAsyncLifetime
{
    private static readonly IPEndPoint Relay = new(IPAddress.Loopback, 5001);
    private static readonly Guid Token = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const uint Ssrc = 4242;

    private readonly ScriptedUdpTransport _transport = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeAudioOutput _output = new();
    private readonly VoiceSession _session;

    public VoiceSessionTests() => _session = new VoiceSession(
        () => _transport,
        _capture,
        _output,
        () => new OpusAudioCodec(),
        TimeProvider.System,
        new ImmediateAudioThread(),
        NullLogger<VoiceSession>.Instance);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _session.DisposeAsync();

    private Task<bool> StartAsync() => _session.StartAsync(Relay, Token, Ssrc);

    [Fact]
    public async Task Start_HandshakesWithTheTokenTheHubIssued()
    {
        (await StartAsync()).Should().BeTrue();

        var handshake = _transport.Sent[0];
        handshake.Destination.Should().Be(Relay);
        VoicePacket.TryReadHandshake(handshake.Payload, out var token).Should().BeTrue();
        token.Should().Be(Token);
    }

    [Fact]
    public async Task Start_WhenTheRelayNeverAnswers_GivesUpWithoutOpeningTheMicrophone()
    {
        // A server reachable on TCP but not UDP is the common port-forwarding mistake.
        _transport.AnswerHandshake = false;

        (await StartAsync()).Should().BeFalse();

        _session.IsActive.Should().BeFalse();
        _capture.IsCapturing.Should().BeFalse();
    }

    [Fact]
    public async Task Start_RetriesTheHandshakeWhenTheFirstAttemptIsLost()
    {
        _transport.HandshakesToIgnore = 2;

        (await StartAsync()).Should().BeTrue();

        _transport.HandshakesReceived.Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task Start_OpensBothDevicesUsingTheConfiguredIds()
    {
        _session.ApplySettings(new AudioSettings { InputDeviceId = "mic-1", OutputDeviceId = "speakers-1" });

        await StartAsync();

        _capture.StartedDeviceId.Should().Be("mic-1");
        _output.StartedDeviceId.Should().Be("speakers-1");
    }

    [Fact]
    public async Task Start_WhenTheMicrophoneCannotBeOpened_ReportsItAndStopsCleanly()
    {
        _capture.StartException = new InvalidOperationException("The device is in use.");
        Exception? reported = null;
        _session.Failed += (_, ex) => reported = ex;

        (await StartAsync()).Should().BeFalse();

        reported!.Message.Should().Be("The device is in use.");
        _session.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task CapturedSpeech_IsEncodedAndSentAsAnAudioPacket()
    {
        await StartAsync();
        _transport.ClearSent();

        await _capture.EmitAsync(AudioSignals.Sine());

        var audio = await _transport.WaitForAudioAsync();
        VoicePacket.TryReadAudio(audio, out var header, out var payload).Should().BeTrue();
        header.Ssrc.Should().Be(Ssrc);
        payload.Length.Should().BeGreaterThan(0).And.BeLessThan(AudioFormat.MaxOpusPayload);
    }

    [Fact]
    public async Task CapturedSpeech_AdvancesSequenceAndTimestampPerFrame()
    {
        await StartAsync();
        _transport.ClearSent();

        await _capture.EmitAsync(AudioSignals.Sine());
        await _transport.WaitForAudioAsync();
        await _capture.EmitAsync(AudioSignals.Sine(startSample: AudioFormat.FrameSamples));
        await _transport.WaitForAudioAsync(2);

        var headers = _transport.AudioHeaders();
        headers[1].Sequence.Should().Be((ushort)(headers[0].Sequence + 1));
        headers[1].TimestampSamples.Should().Be(headers[0].TimestampSamples + AudioFormat.FrameSamples);
    }

    [Fact]
    public async Task CapturedSilence_IsNotTransmitted()
    {
        // The voice gate is what keeps an idle channel from costing bandwidth.
        await StartAsync();
        _transport.ClearSent();

        await _capture.EmitAsync(AudioSignals.Silence());

        _transport.AudioHeaders().Should().BeEmpty();
    }

    [Fact]
    public async Task CapturedSpeech_WhileMuted_IsNotTransmitted()
    {
        await StartAsync();
        _transport.ClearSent();
        _session.IsMuted = true;

        await _capture.EmitAsync(AudioSignals.Sine());

        _transport.AudioHeaders().Should().BeEmpty();
        _session.IsTransmitting.Should().BeFalse();
    }

    [Fact]
    public async Task CapturedSpeech_WithPushToTalkAndTheKeyUp_IsNotTransmitted()
    {
        await StartAsync();
        _session.ApplySettings(new AudioSettings { UsePushToTalk = true });
        _transport.ClearSent();

        await _capture.EmitAsync(AudioSignals.Sine());

        _transport.AudioHeaders().Should().BeEmpty();
    }

    [Fact]
    public async Task CapturedSpeech_WithPushToTalkAndTheKeyDown_IsTransmitted()
    {
        await StartAsync();
        _session.ApplySettings(new AudioSettings { UsePushToTalk = true });
        _session.SetPushToTalkPressed(true);
        _transport.ClearSent();

        await _capture.EmitAsync(AudioSignals.Sine());

        (await _transport.WaitForAudioAsync()).Length.Should().BeGreaterThan(VoicePacket.AudioHeaderSize);
    }

    [Fact]
    public async Task CapturedAudio_RaisesTheInputLevelForTheMeter()
    {
        await StartAsync();
        var levels = new List<float>();
        _session.InputLevelChanged += (_, level) => levels.Add(level);

        await _capture.EmitAsync(AudioSignals.Sine());

        levels.Should().NotBeEmpty();
        levels[^1].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task IncomingAudio_FromAPeer_IsDecodedAndPlayedBack()
    {
        await StartAsync();

        // Enough frames to fill the jitter buffer, which deliberately holds playback back
        // until it has some slack.
        for (ushort sequence = 0; sequence < 4; sequence++)
        {
            _transport.Deliver(AudioPacket(ssrc: 99, sequence), Relay);
        }

        await WaitUntilAsync(PeerIsAudible);
    }

    [Fact]
    public async Task IncomingAudio_ThatEchoesTheLocalSsrc_IsIgnored()
    {
        // The relay never echoes, so a packet claiming our own ssrc is spoofed.
        await StartAsync();

        for (ushort sequence = 0; sequence < 4; sequence++)
        {
            _transport.Deliver(AudioPacket(Ssrc, sequence), Relay);
        }

        await Task.Delay(100);

        ReadOutput().Should().AllSatisfy(sample => sample.Should().Be(0f));
    }

    [Fact]
    public async Task IncomingAudio_FromSomewhereOtherThanTheRelay_IsIgnored()
    {
        await StartAsync();

        var stranger = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 40000);

        for (ushort sequence = 0; sequence < 4; sequence++)
        {
            _transport.Deliver(AudioPacket(ssrc: 99, sequence), stranger);
        }

        await Task.Delay(100);

        ReadOutput().Should().AllSatisfy(sample => sample.Should().Be(0f));
    }

    [Fact]
    public async Task Stop_EndsTheUdpSessionButKeepsDevicesWarm()
    {
        await StartAsync();

        await _session.StopAsync();

        _session.IsActive.Should().BeFalse();
        _capture.IsCapturing.Should().BeTrue();
        _output.IsPlaying.Should().BeTrue();
        _transport.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_ThenStart_ReusesTheOpenDevices()
    {
        await StartAsync();
        await _session.StopAsync();

        (await StartAsync()).Should().BeTrue();

        _capture.StartCount.Should().Be(1);
        _output.StartCount.Should().Be(1);
        _session.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_WhenReleasingDevices_ClosesTheMicrophone()
    {
        await StartAsync();

        await _session.StopAsync(releaseDevices: true);

        _capture.IsCapturing.Should().BeFalse();
        _output.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_ThenCapture_TransmitsNothing()
    {
        await StartAsync();
        await _session.StopAsync();
        _transport.ClearSent();

        await _capture.EmitAsync(AudioSignals.Sine());

        _transport.AudioHeaders().Should().BeEmpty();
    }

    [Fact]
    public async Task DeviceFailureMidCall_IsReportedRatherThanCrashingTheAudioThread()
    {
        await StartAsync();
        Exception? reported = null;
        _session.Failed += (_, ex) => reported = ex;

        _capture.RaiseFailed(new InvalidOperationException("The headset was unplugged."));

        reported!.Message.Should().Be("The headset was unplugged.");
    }

    [Fact]
    public async Task ApplySettings_WhileRunning_UpdatesTheJitterDepthForNewPeers()
    {
        await StartAsync();

        _session.ApplySettings(new AudioSettings { JitterBufferFrames = 6 });
        _transport.Deliver(AudioPacket(ssrc: 99, sequence: 0), Relay);
        await Task.Delay(50);

        _session.Settings.JitterBufferFrames.Should().Be(6);
    }

    private static byte[] AudioPacket(uint ssrc, ushort sequence)
    {
        using var codec = new OpusAudioCodec();
        var payload = new byte[AudioFormat.MaxOpusPayload];
        var encoded = codec.Encode(AudioSignals.Sine(startSample: sequence * AudioFormat.FrameSamples), payload);

        var packet = new byte[VoicePacket.MaxPacketSize];
        var length = VoicePacket.WriteAudio(
            packet,
            ssrc,
            sequence,
            sequence * (uint)AudioFormat.FrameSamples,
            payload.AsSpan(0, encoded));

        return packet[..length];
    }

    private float[] ReadOutput()
    {
        var block = new float[AudioFormat.FrameSamples];
        _output.Source?.Read(block);
        return block;
    }

    private bool PeerIsAudible() => AudioSignals.RmsDb(ReadOutput()) > -60;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // The receive loop runs on its own thread, so the assertion has to wait for it.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The expected audio never reached the output.");
    }
}

/// <summary>
/// A transport that plays the relay's part: it acknowledges handshakes and can deliver
/// datagrams back to the session, with no socket in the picture.
/// </summary>
internal sealed class ScriptedUdpTransport : IUdpTransport
{
    private readonly Channel<(byte[] Payload, IPEndPoint Source)> _inbound =
        Channel.CreateUnbounded<(byte[], IPEndPoint)>();

    private readonly Lock _gate = new();
    private readonly List<(byte[] Payload, IPEndPoint Destination)> _sent = [];

    public bool AnswerHandshake { get; set; } = true;

    /// <summary>Simulates handshake packets lost on the way out.</summary>
    public int HandshakesToIgnore { get; set; }

    public int HandshakesReceived { get; private set; }

    public bool IsDisposed { get; private set; }

    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>Snapshot, because the send loop keeps appending on its own thread.</summary>
    public List<(byte[] Payload, IPEndPoint Destination)> Sent => Snapshot();

    public void Bind(IPEndPoint endPoint) => LocalEndPoint = endPoint;

    public void Deliver(byte[] payload, IPEndPoint source) => _inbound.Writer.TryWrite((payload, source));

    public ValueTask SendToAsync(
        ReadOnlyMemory<byte> buffer,
        IPEndPoint destination,
        CancellationToken cancellationToken = default)
    {
        var payload = buffer.ToArray();

        lock (_gate)
        {
            _sent.Add((payload, destination));
        }

        if (VoicePacket.TryPeekType(payload, out var type) && type == VoiceMessageType.Handshake)
        {
            HandshakesReceived++;

            if (AnswerHandshake && HandshakesReceived > HandshakesToIgnore)
            {
                var ack = new byte[VoicePacket.HandshakeAckSize];
                VoicePacket.WriteHandshakeAck(ack, 4242);
                Deliver(ack, destination);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<UdpDatagram> ReceiveFromAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var (payload, source) = await _inbound.Reader.ReadAsync(cancellationToken);
        payload.CopyTo(buffer);
        return new UdpDatagram(payload.Length, source);
    }

    public void Dispose() => IsDisposed = true;

    public List<VoiceAudioHeader> AudioHeaders()
    {
        var headers = new List<VoiceAudioHeader>();

        foreach (var (payload, _) in Snapshot())
        {
            if (VoicePacket.TryReadAudio(payload, out var header, out _))
            {
                headers.Add(header);
            }
        }

        return headers;
    }

    public async Task<byte[]> WaitForAudioAsync(int count = 1)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var audio = Snapshot()
                .Where(s => VoicePacket.TryPeekType(s.Payload, out var type) && type == VoiceMessageType.Audio)
                .ToList();

            if (audio.Count >= count)
            {
                return audio[count - 1].Payload;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Only saw fewer than {count} audio packets.");
    }

    public void ClearSent()
    {
        lock (_gate)
        {
            _sent.Clear();
        }
    }

    private List<(byte[] Payload, IPEndPoint Destination)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _sent];
        }
    }
}

internal sealed class FakeAudioCapture : IAudioCapture
{
    public bool IsCapturing { get; private set; }

    public string? StartedDeviceId { get; private set; }

    public Exception? StartException { get; set; }

    public event EventHandler<short[]>? FrameCaptured;

    public event EventHandler<Exception>? Failed;

    public int StartCount { get; private set; }

    public void Start(string? deviceId)
    {
        if (StartException is { } failure)
        {
            throw failure;
        }

        StartedDeviceId = deviceId;
        IsCapturing = true;
        StartCount++;
    }

    public void Stop() => IsCapturing = false;

    public void Dispose() => Stop();

    /// <summary>Pushes a frame the way the capture thread would.</summary>
    public Task EmitAsync(short[] frame)
    {
        FrameCaptured?.Invoke(this, frame);
        return Task.CompletedTask;
    }

    public void RaiseFailed(Exception exception) => Failed?.Invoke(this, exception);
}

internal sealed class FakeAudioOutput : IAudioOutput
{
    public bool IsPlaying { get; private set; }

    public string? StartedDeviceId { get; private set; }

    public IFrameSource? Source { get; private set; }

    public event EventHandler<Exception>? Failed;

    public int StartCount { get; private set; }

    public void Start(string? deviceId, IFrameSource source)
    {
        StartedDeviceId = deviceId;
        Source = source;
        IsPlaying = true;
        StartCount++;
    }

    public void Stop() => IsPlaying = false;

    public void Dispose() => Stop();

    public void RaiseFailed(Exception exception) => Failed?.Invoke(this, exception);
}
