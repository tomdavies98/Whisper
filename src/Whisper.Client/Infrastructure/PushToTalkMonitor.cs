using System.Runtime.InteropServices;

namespace Whisper.Client.Infrastructure;

/// <summary>
/// Watches one key globally. Polling <c>GetAsyncKeyState</c> rather than installing a
/// keyboard hook keeps push-to-talk working while another window has focus, without the
/// app behaving like a keylogger: only the configured key is ever read.
/// </summary>
public sealed class PushToTalkMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly Lock _gate = new();
    private Timer? _timer;
    private bool _isPressed;

    /// <summary>Virtual key code to watch. Defaults to left control.</summary>
    public int VirtualKey { get; set; } = 0xA2;

    public bool IsPressed => _isPressed;

    public event EventHandler<bool>? PressedChanged;

    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(Poll, null, PollInterval, PollInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        if (_isPressed)
        {
            _isPressed = false;
            PressedChanged?.Invoke(this, false);
        }
    }

    public void Dispose() => Stop();

    private void Poll(object? state)
    {
        var pressed = (GetAsyncKeyState(VirtualKey) & 0x8000) != 0;

        if (pressed == _isPressed)
        {
            return;
        }

        _isPressed = pressed;
        PressedChanged?.Invoke(this, pressed);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
