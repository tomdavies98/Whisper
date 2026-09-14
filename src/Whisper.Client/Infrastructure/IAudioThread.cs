namespace Whisper.Client.Infrastructure;

/// <summary>
/// Runs WASAPI start/stop on one dedicated thread. Capture and playback COM objects are
/// thread-affine: creating them on a thread-pool worker and stopping them on another is
/// what froze the client after leave-voice, when the next join hung in StartRecording.
/// </summary>
public interface IAudioThread
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
