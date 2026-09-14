namespace Whisper.Shared;

/// <summary>
/// The single audio format every Whisper endpoint speaks. Clients never negotiate:
/// mismatched frame sizes would desynchronise the jitter buffer on the receiving side.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameMilliseconds = 20;
    public const int FrameSamples = SampleRate / 1000 * FrameMilliseconds;
    public const int BitsPerSample = 16;

    /// <summary>Largest payload a single Opus frame can produce at this frame size.</summary>
    public const int MaxOpusPayload = 1275;
}
