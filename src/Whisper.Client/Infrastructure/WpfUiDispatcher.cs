using System.Windows;
using System.Windows.Threading;

namespace Whisper.Client.Infrastructure;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public bool IsOnUiThread => Dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (IsOnUiThread)
        {
            action();
            return;
        }

        Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public Task InvokeAsync(Action action) =>
        IsOnUiThread ? RunInline(action) : Dispatcher.InvokeAsync(action).Task;

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
