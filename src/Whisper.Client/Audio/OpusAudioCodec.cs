using Concentus;
using Concentus.Enums;
using Whisper.Shared;

namespace Whisper.Client.Audio;

public sealed class OpusAudioCodec : IAudioCodec
{
    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;

    public OpusAudioCodec(int bitrate = 40_000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(
            AudioFormat.SampleRate,
            AudioFormat.Channels,
            OpusApplication.OPUS_APPLICATION_VOIP);

        _encoder.Bitrate = bitrate;
        _encoder.UseVBR = true;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.Complexity = 5;

        // In-band forward error correction lets the decoder rebuild a dropped frame from
        // the next one, which matters far more than bitrate on a lossy home connection.
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;

        _decoder = OpusCodecFactory.CreateDecoder(AudioFormat.SampleRate, AudioFormat.Channels);
    }

    public int FrameSamples => AudioFormat.FrameSamples;

    public int Encode(ReadOnlySpan<short> pcm, Span<byte> destination)
    {
        if (pcm.Length != AudioFormat.FrameSamples)
        {
            throw new ArgumentException(
                $"Opus expects exactly {AudioFormat.FrameSamples} samples per frame.",
                nameof(pcm));
        }

        return _encoder.Encode(pcm, AudioFormat.FrameSamples, destination, destination.Length);
    }

    public int Decode(ReadOnlySpan<byte> payload, Span<short> destination) =>
        _decoder.Decode(payload, destination, AudioFormat.FrameSamples, decode_fec: false);

    public int DecodeLost(Span<short> destination) =>
        _decoder.Decode(ReadOnlySpan<byte>.Empty, destination, AudioFormat.FrameSamples, decode_fec: false);

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
