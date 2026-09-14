namespace Whisper.Client.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

public interface IAudioDeviceProvider
{
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
}

/// <summary>
/// Microphone capture, normalised to the single format the protocol speaks:
/// <see cref="Whisper.Shared.AudioFormat.FrameSamples"/> mono 16-bit samples at 48 kHz.
/// </summary>
public interface IAudioCapture : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>Raised once per 20 ms frame, on a capture thread rather than the UI thread.</summary>
    event EventHandler<short[]>? FrameCaptured;

    /// <summary>Windows mixer peak 0..1 for the selected capture endpoint.</summary>
    event EventHandler<float>? EndpointPeakChanged;

    /// <summary>Raised when the device fails or is removed mid-call.</summary>
    event EventHandler<Exception>? Failed;

    void Start(string? deviceId);

    void Stop();
}

/// <summary>Speaker playback driven by a caller-supplied mixer.</summary>
public interface IAudioOutput : IDisposable
{
    bool IsPlaying { get; }

    event EventHandler<Exception>? Failed;

    void Start(string? deviceId, IFrameSource source);

    void Stop();
}

/// <summary>
/// Supplies mixed audio on demand. The output device pulls from this, which is what keeps
/// playback paced by the sound card rather than by a timer that will inevitably drift.
/// </summary>
public interface IFrameSource
{
    /// <summary>Fills <paramref name="destination"/> with mono 48 kHz samples, padding with silence.</summary>
    void Read(Span<float> destination);
}
