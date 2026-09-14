using FluentAssertions;
using Whisper.Client.Audio;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Client;

public class OpusAudioCodecTests
{
    private const int SkippedFrames = 4;

    /// <summary>
    /// Encodes and decodes a continuous tone, returning both streams from the point where
    /// the codec has converged. Comparing streams rather than a single frame is what makes
    /// the codec's algorithmic delay measurable instead of fatal.
    /// </summary>
    private static (short[] Original, short[] Decoded) RoundTripStream(int frames = 20)
    {
        using var encoder = new OpusAudioCodec();
        using var decoder = new OpusAudioCodec();

        var packet = new byte[AudioFormat.MaxOpusPayload];
        var original = new short[frames * AudioFormat.FrameSamples];
        var decoded = new short[frames * AudioFormat.FrameSamples];

        for (var i = 0; i < frames; i++)
        {
            var offset = i * AudioFormat.FrameSamples;
            var frame = AudioSignals.Sine(startSample: offset);
            frame.CopyTo(original, offset);

            var encodedLength = encoder.Encode(frame, packet);
            decoder.Decode(packet.AsSpan(0, encodedLength), decoded.AsSpan(offset, AudioFormat.FrameSamples));
        }

        var skip = SkippedFrames * AudioFormat.FrameSamples;
        return (original[skip..], decoded[skip..]);
    }

    [Fact]
    public void Encode_ProducesAPayloadThatFitsTheProtocolLimit()
    {
        using var codec = new OpusAudioCodec();
        var packet = new byte[AudioFormat.MaxOpusPayload];

        var length = codec.Encode(AudioSignals.Sine(), packet);

        length.Should().BeGreaterThan(0);
        length.Should().BeLessThanOrEqualTo(AudioFormat.MaxOpusPayload);
    }

    [Fact]
    public void Encode_CompressesAFrameToAFractionOfItsPcmSize()
    {
        using var codec = new OpusAudioCodec();
        var packet = new byte[AudioFormat.MaxOpusPayload];

        var length = codec.Encode(AudioSignals.Sine(), packet);

        // 960 samples of 16-bit PCM is 1920 bytes on the wire without a codec.
        length.Should().BeLessThan(AudioFormat.FrameSamples * 2 / 4);
    }

    [Fact]
    public void Decode_ReturnsExactlyOneFrameOfSamples()
    {
        using var encoder = new OpusAudioCodec();
        using var decoder = new OpusAudioCodec();
        var packet = new byte[AudioFormat.MaxOpusPayload];
        var decoded = new short[AudioFormat.FrameSamples];

        var length = encoder.Encode(AudioSignals.Sine(), packet);
        var samples = decoder.Decode(packet.AsSpan(0, length), decoded);

        samples.Should().Be(AudioFormat.FrameSamples);
    }

    [Fact]
    public void RoundTrip_OfASineWave_ReproducesTheWaveform()
    {
        var (original, decoded) = RoundTripStream();

        // One frame of search covers the encoder lookahead with room to spare.
        AudioSignals.BestCorrelation(original, decoded, AudioFormat.FrameSamples)
            .Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void RoundTrip_PreservesTheSignalLevel()
    {
        var (original, decoded) = RoundTripStream();

        AudioSignals.RmsDb(decoded).Should().BeApproximately(AudioSignals.RmsDb(original), 3.0);
    }

    [Fact]
    public void RoundTrip_OfSilence_StaysSilent()
    {
        using var encoder = new OpusAudioCodec();
        using var decoder = new OpusAudioCodec();
        var packet = new byte[AudioFormat.MaxOpusPayload];
        var decoded = new short[AudioFormat.FrameSamples];

        var length = encoder.Encode(AudioSignals.Silence(), packet);
        decoder.Decode(packet.AsSpan(0, length), decoded);

        AudioSignals.RmsDb(decoded).Should().BeLessThan(-60);
    }

    [Fact]
    public void Encode_WithTheWrongFrameSize_IsRejected()
    {
        // A mismatched frame size would desynchronise every receiver's jitter buffer, so
        // it fails loudly at the source instead.
        using var codec = new OpusAudioCodec();
        var packet = new byte[AudioFormat.MaxOpusPayload];

        var encode = () => codec.Encode(new short[480], packet);

        encode.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DecodeLost_ProducesAFullFrameOfConcealmentAudio()
    {
        using var encoder = new OpusAudioCodec();
        using var decoder = new OpusAudioCodec();
        var packet = new byte[AudioFormat.MaxOpusPayload];
        var decoded = new short[AudioFormat.FrameSamples];

        for (var i = 0; i < 5; i++)
        {
            var length = encoder.Encode(AudioSignals.Sine(startSample: i * AudioFormat.FrameSamples), packet);
            decoder.Decode(packet.AsSpan(0, length), decoded);
        }

        var concealed = new short[AudioFormat.FrameSamples];
        var samples = decoder.DecodeLost(concealed);

        samples.Should().Be(AudioFormat.FrameSamples);
        AudioSignals.RmsDb(concealed).Should().BeGreaterThan(-60, "concealment should extrapolate speech, not insert a silent click");
    }

    [Fact]
    public void Decode_OfGarbagePayload_DoesNotCorruptLaterFrames()
    {
        using var encoder = new OpusAudioCodec();
        using var decoder = new OpusAudioCodec();
        var packet = new byte[AudioFormat.MaxOpusPayload];
        var decoded = new short[AudioFormat.FrameSamples];

        try
        {
            decoder.Decode([0xFF, 0xFF, 0xFF, 0xFF], decoded);
        }
        catch (Exception)
        {
            // Rejecting a corrupt packet is fine; silently accepting it would not be.
        }

        var length = encoder.Encode(AudioSignals.Sine(), packet);
        decoder.Decode(packet.AsSpan(0, length), decoded).Should().Be(AudioFormat.FrameSamples);
    }
}
