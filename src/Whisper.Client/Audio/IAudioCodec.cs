namespace Whisper.Client.Audio;

/// <summary>
/// Frame-at-a-time speech codec. Opus is stateful and not thread safe, so one instance
/// belongs to exactly one stream: the microphone, or one remote speaker.
/// </summary>
public interface IAudioCodec : IDisposable
{
    int FrameSamples { get; }

    /// <summary>Encodes exactly one frame. Returns the number of bytes written.</summary>
    int Encode(ReadOnlySpan<short> pcm, Span<byte> destination);

    /// <summary>Decodes one packet. Returns the number of samples written.</summary>
    int Decode(ReadOnlySpan<byte> payload, Span<short> destination);

    /// <summary>
    /// Synthesises a replacement for a frame that never arrived, which sounds far better
    /// than the click a silent gap produces.
    /// </summary>
    int DecodeLost(Span<short> destination);
}
