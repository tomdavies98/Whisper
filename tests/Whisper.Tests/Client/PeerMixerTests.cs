using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Whisper.Client.Audio;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Client;

public class PeerMixerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly PeerMixer _mixer;

    public PeerMixerTests()
    {
        _mixer = new PeerMixer(() => new OpusAudioCodec(), _time) { JitterBufferFrames = 1 };
    }

    /// <summary>Encodes a tone the way a remote client would before it hits the wire.</summary>
    private static byte[] EncodeTone(double frequency, double amplitude = 0.5, int frameIndex = 0)
    {
        using var codec = new OpusAudioCodec();
        var payload = new byte[AudioFormat.MaxOpusPayload];
        var frame = AudioSignals.Sine(
            frequency,
            amplitude,
            startSample: frameIndex * AudioFormat.FrameSamples);
        var length = codec.Encode(frame, payload);

        return payload[..length];
    }

    private float[] ReadFrame()
    {
        var block = new float[AudioFormat.FrameSamples];
        _mixer.Read(block);
        return block;
    }

    [Fact]
    public void Read_WithNoPeers_ProducesSilence()
    {
        ReadFrame().Should().AllSatisfy(sample => sample.Should().Be(0f));
    }

    [Fact]
    public void Read_WithOnePeerSpeaking_ProducesAudio()
    {
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));

        AudioSignals.RmsDb(ReadFrame()).Should().BeGreaterThan(-40);
    }

    [Fact]
    public void Read_WithTwoPeersSpeaking_IsLouderThanEitherAlone()
    {
        // Proves both streams are summed rather than one overwriting the other.
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));
        var single = AudioSignals.RmsDb(ReadFrame());

        _mixer.Clear();
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));
        _mixer.Enqueue(ssrc: 2, sequence: 0, EncodeTone(440));
        var both = AudioSignals.RmsDb(ReadFrame());

        both.Should().BeGreaterThan(single + 2);
    }

    [Fact]
    public void Read_NeverExceedsFullScale()
    {
        // Several loud speakers at once must clamp, not wrap around into distortion.
        for (uint ssrc = 1; ssrc <= 6; ssrc++)
        {
            _mixer.Enqueue(ssrc, sequence: 0, EncodeTone(440, amplitude: 0.95));
        }

        ReadFrame().Should().AllSatisfy(sample => Math.Abs(sample).Should().BeLessThanOrEqualTo(1f));
    }

    [Fact]
    public void Read_WhenDeafened_ProducesSilenceEvenWhilePacketsArrive()
    {
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));
        _mixer.IsDeafened = true;

        ReadFrame().Should().AllSatisfy(sample => sample.Should().Be(0f));
    }

    [Fact]
    public void Read_ForAPartialBlock_CarriesTheRemainderIntoTheNextCall()
    {
        // The output device asks for whatever its buffer needs, not whole 20 ms frames.
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));
        _mixer.Enqueue(ssrc: 1, sequence: 1, EncodeTone(440, frameIndex: 1));

        var half = new float[AudioFormat.FrameSamples / 2];
        _mixer.Read(half);
        var second = new float[AudioFormat.FrameSamples / 2];
        _mixer.Read(second);

        AudioSignals.RmsDb(half).Should().BeGreaterThan(-40);
        AudioSignals.RmsDb(second).Should().BeGreaterThan(-40);
    }

    [Fact]
    public void Enqueue_GivesEachSpeakerItsOwnDecoderAndBuffer()
    {
        // Opus is stateful; one decoder shared between speakers would produce garbage.
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));
        _mixer.Enqueue(ssrc: 2, sequence: 0, EncodeTone(880));

        _mixer.ActiveSsrcs.Should().BeEquivalentTo([1u, 2u]);
        _mixer.BufferFor(1).Should().NotBeSameAs(_mixer.BufferFor(2));
    }

    [Fact]
    public void Enqueue_AppliesTheConfiguredJitterDepthToNewPeers()
    {
        _mixer.JitterBufferFrames = 5;

        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));

        _mixer.BufferFor(1)!.TargetFrames.Should().Be(5);
    }

    [Fact]
    public void Enqueue_WithACorruptPayload_SkipsTheFrameInsteadOfFailing()
    {
        // A mangled packet must not take down the audio thread.
        _mixer.Enqueue(ssrc: 1, sequence: 0, [0xFF, 0xFF, 0xFF, 0xFF]);

        var read = () => ReadFrame();

        read.Should().NotThrow();
    }

    [Fact]
    public void Remove_DropsThePeerSoTheyStopBeingMixed()
    {
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));

        _mixer.Remove(1);

        _mixer.ActiveSsrcs.Should().BeEmpty();
        ReadFrame().Should().AllSatisfy(sample => sample.Should().Be(0f));
    }

    [Fact]
    public void Clear_DropsEveryPeer()
    {
        _mixer.Enqueue(ssrc: 1, sequence: 0, EncodeTone(440));
        _mixer.Enqueue(ssrc: 2, sequence: 0, EncodeTone(440));

        _mixer.Clear();

        _mixer.ActiveSsrcs.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_WhileTheOutputThreadIsReading_DoesNotThrow()
    {
        // Leave-voice used to dispose codecs under an in-flight WASAPI Read().
        using var running = new CancellationTokenSource();
        var block = new float[AudioFormat.FrameSamples];
        var reader = Task.Run(() =>
        {
            while (!running.Token.IsCancellationRequested)
            {
                _mixer.Read(block);
            }
        });

        for (ushort sequence = 0; sequence < 8; sequence++)
        {
            _mixer.Enqueue(1, sequence, EncodeTone(440, frameIndex: sequence));
            _mixer.Enqueue(2, sequence, EncodeTone(880, frameIndex: sequence));
        }

        var clear = () => _mixer.Clear();
        clear.Should().NotThrow();

        await running.CancelAsync();
        await reader;
    }
}
