using System.Collections.Concurrent;

namespace Whisper.Client.Infrastructure;

/// <summary>
/// One long-lived MTA thread owns every WASAPI object for the life of the process.
/// Thread-pool workers are MTA too, but each <c>Task.Run</c> is a different thread;
/// creating capture on one worker and disposing it on another hangs inside COM, and
/// the next join then blocks forever in <c>StartRecording</c>.
/// SynchronizationContext is kept null so NAudio cannot marshal callbacks onto this
/// thread while it is blocked in StopRecording.
/// </summary>
public sealed class DedicatedAudioThread : IAudioThread, IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public DedicatedAudioThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Whisper.Audio",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var item = new WorkItem(action, cancellationToken);

        try
        {
            _queue.Add(item, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(DedicatedAudioThread));
        }

        return item.Completion.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
        _queue.Dispose();
    }

    private void Run()
    {
        // NAudio captures Current at construction time. A dispatcher context would post
        // DataAvailable here, and StopRecording would deadlock waiting for it.
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Run();
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private sealed class WorkItem(Action action, CancellationToken cancellationToken)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }
}
