namespace Whisper.Client.Infrastructure;

/// <summary>
/// Marshals work onto the UI thread. View models depend on this rather than
/// <c>Dispatcher</c> directly, which is what lets them be unit tested with no WPF
/// message loop running.
/// </summary>
public interface IUiDispatcher
{
    bool IsOnUiThread { get; }

    void Post(Action action);

    Task InvokeAsync(Action action);
}
