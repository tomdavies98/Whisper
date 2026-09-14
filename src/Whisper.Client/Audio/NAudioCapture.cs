using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.Shared;

namespace Whisper.Client.Audio;

/// <summary>
/// Microphone capture owned by one long-lived MTA thread. Create, poll, stop and dispose
/// of the WASAPI client all happen on that thread, so nothing ever Joins a nested capture
/// thread from elsewhere (that was freezing the app on join / settings).
/// Packets are always copied — NAudio's WasapiCapture zeroes buffers the driver marks
/// Silent, which left Arctis and similar headsets looking dead.
/// </summary>
public sealed class NAudioCapture : IAudioCapture
{
    private const int PollMilliseconds = 30;
    private const long HundredNsPerMs = 10_000;

    private readonly ConcurrentQueue<WorkItem> _work = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly short[] _frame = new short[AudioFormat.FrameSamples];
    private readonly float[] _scratch = new float[AudioFormat.FrameSamples];

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioClient? _audioClient;
    private AudioCaptureClient? _captureClient;
    private EventWaitHandle? _frameEvent;
    private WaveFormat? _mixFormat;
    private byte[] _packetBuffer = [];
    private int _bytesPerFrame;
    private int _framePosition;
    private volatile bool _capturing;
    private volatile bool _disposed;
    private float _gain = 1f;

    public NAudioCapture()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Whisper.Capture",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsCapturing => _capturing;

    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    public event EventHandler<short[]>? FrameCaptured;

    public event EventHandler<Exception>? Failed;

    public event EventHandler<float>? EndpointPeakChanged;

    public void Start(string? deviceId) =>
        Invoke(() => StartCore(deviceId));

    public void Stop() =>
        Invoke(StopCore);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Invoke(StopCore, allowWhenDisposed: true);
        }
        catch (Exception)
        {
            // Shutting down.
        }

        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }

    private void Invoke(Action action, bool allowWhenDisposed = false)
    {
        if (_disposed && !allowWhenDisposed)
        {
            throw new ObjectDisposedException(nameof(NAudioCapture));
        }

        var item = new WorkItem(action);
        _work.Enqueue(item);

        try
        {
            _frameEvent?.Set();
        }
        catch (Exception)
        {
            // Event already torn down.
        }

        _wake.Set();

        if (!item.Completion.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The microphone did not respond in time.");
        }

        item.Completion.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        while (!_disposed)
        {
            while (_work.TryDequeue(out var item))
            {
                item.Run();
            }

            if (!_capturing)
            {
                _wake.WaitOne(250);
                continue;
            }

            try
            {
                _frameEvent?.WaitOne(PollMilliseconds);
                DrainPackets();
                PublishPeak();
            }
            catch (Exception ex)
            {
                Failed?.Invoke(this, ex);
                StopCore();
            }
        }

        StopCore();
    }

    private void StartCore(string? deviceId)
    {
        StopCore();

        var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = NAudioDeviceProvider.Resolve(enumerator, deviceId, DataFlow.Capture)
                ?? throw new InvalidOperationException("No microphone is available.");
            Unmute(device);
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }

        AudioClient audioClient;
        EventWaitHandle frameEvent;
        try
        {
            audioClient = device.AudioClient;
            var mixFormat = audioClient.MixFormat;
            frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback
                    | AudioClientStreamFlags.AutoConvertPcm
                    | AudioClientStreamFlags.SrcDefaultQuality,
                50 * HundredNsPerMs,
                0,
                mixFormat,
                Guid.Empty);

            audioClient.SetEventHandle(frameEvent.SafeWaitHandle.DangerousGetHandle());

            _bytesPerFrame = Math.Max(1, mixFormat.BlockAlign);
            _packetBuffer = new byte[Math.Max(audioClient.BufferSize * _bytesPerFrame, _bytesPerFrame)];
            _mixFormat = mixFormat;
            _captureClient = audioClient.AudioCaptureClient;
            _framePosition = 0;

            audioClient.Start();
        }
        catch
        {
            device.Dispose();
            enumerator.Dispose();
            throw;
        }

        _enumerator = enumerator;
        _device = device;
        _audioClient = audioClient;
        _frameEvent = frameEvent;
        _capturing = true;
    }

    private void StopCore()
    {
        _capturing = false;

        var client = _audioClient;
        var captureClient = _captureClient;
        var frameEvent = _frameEvent;
        var device = _device;
        var enumerator = _enumerator;

        _audioClient = null;
        _captureClient = null;
        _frameEvent = null;
        _device = null;
        _enumerator = null;
        _mixFormat = null;

        try
        {
            client?.Stop();
        }
        catch (Exception)
        {
            // Already stopped.
        }

        // Capture client is owned by AudioClient; disposing the client is enough.
        _ = captureClient;
        client?.Dispose();
        frameEvent?.Dispose();
        device?.Dispose();
        enumerator?.Dispose();
        _framePosition = 0;
    }

    private void DrainPackets()
    {
        var captureClient = _captureClient;
        var format = _mixFormat;

        if (captureClient is null || format is null)
        {
            return;
        }

        var packetSize = captureClient.GetNextPacketSize();

        while (packetSize != 0 && _capturing)
        {
            var pointer = captureClient.GetBuffer(out var frames, out _);
            var bytes = frames * _bytesPerFrame;

            if (bytes > _packetBuffer.Length)
            {
                bytes = _packetBuffer.Length;
                frames = bytes / _bytesPerFrame;
            }

            if (bytes > 0 && pointer != IntPtr.Zero)
            {
                Marshal.Copy(pointer, _packetBuffer, 0, bytes);
                AcceptSamples(_packetBuffer.AsSpan(0, bytes), format);
            }

            captureClient.ReleaseBuffer(frames);
            packetSize = captureClient.GetNextPacketSize();
        }
    }

    private void AcceptSamples(ReadOnlySpan<byte> source, WaveFormat format)
    {
        var frames = new List<short[]>();
        var offset = 0;

        while (offset < source.Length)
        {
            var sample = ReadSample(source, ref offset, format);
            _scratch[_framePosition] = Math.Clamp(sample * _gain, -1f, 1f);
            _frame[_framePosition] = (short)(_scratch[_framePosition] * short.MaxValue);
            _framePosition++;

            if (_framePosition < AudioFormat.FrameSamples)
            {
                continue;
            }

            _framePosition = 0;
            frames.Add(_frame.AsSpan().ToArray());
        }

        foreach (var frame in frames)
        {
            FrameCaptured?.Invoke(this, frame);
        }
    }

    private static float ReadSample(ReadOnlySpan<byte> source, ref int offset, WaveFormat format)
    {
        var isFloat = format.BitsPerSample == 32 && format.Encoding != WaveFormatEncoding.Pcm;

        if (isFloat)
        {
            var sum = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                if (offset + 4 > source.Length)
                {
                    offset = source.Length;
                    return 0f;
                }

                sum += BitConverter.ToSingle(source[offset..(offset + 4)]);
                offset += 4;
            }

            return sum / Math.Max(1, format.Channels);
        }

        if (format.BitsPerSample == 16)
        {
            var sum = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                if (offset + 2 > source.Length)
                {
                    offset = source.Length;
                    return 0f;
                }

                sum += BitConverter.ToInt16(source[offset..(offset + 2)]) / (float)short.MaxValue;
                offset += 2;
            }

            return sum / Math.Max(1, format.Channels);
        }

        offset = Math.Min(source.Length, offset + Math.Max(1, format.BlockAlign));
        return 0f;
    }

    private void PublishPeak()
    {
        try
        {
            var peak = _device?.AudioMeterInformation.MasterPeakValue ?? 0f;
            EndpointPeakChanged?.Invoke(this, peak);
        }
        catch (Exception)
        {
            // Endpoint can go away mid-call.
        }
    }

    private static void Unmute(MMDevice device)
    {
        try
        {
            var volume = device.AudioEndpointVolume;
            volume.Mute = false;

            if (volume.MasterVolumeLevelScalar < 0.2f)
            {
                volume.MasterVolumeLevelScalar = 0.8f;
            }
        }
        catch (Exception)
        {
            // Virtual devices may not expose endpoint volume.
        }
    }

    private sealed class WorkItem(Action action)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run()
        {
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
