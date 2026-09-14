using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.Shared;

namespace Whisper.Client.Audio;

/// <summary>
/// WASAPI capture normalised to 48 kHz mono. Microphones report whatever format they like,
/// so every buffer goes through a downmix and resample stage before being cut into the
/// fixed 20 ms frames the protocol requires.
/// </summary>
public sealed class NAudioCapture : IAudioCapture
{
    private readonly short[] _frame = new short[AudioFormat.FrameSamples];
    private readonly float[] _scratch = new float[AudioFormat.FrameSamples];

    private readonly Lock _gate = new();

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _pipeline;
    private int _framePosition;
    private volatile bool _stopping;
    private bool _disposed;

    public bool IsCapturing => _capture is not null;

    public float Gain { get; set; } = 1f;

    public event EventHandler<short[]>? FrameCaptured;

    public event EventHandler<Exception>? Failed;

    public void Start(string? deviceId)
    {
        Stop();
        _stopping = false;

        var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = NAudioDeviceProvider.Resolve(enumerator, deviceId, DataFlow.Capture)
                ?? throw new InvalidOperationException("No microphone is available.");
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }

        var capture = new WasapiCapture(device, useEventSync: false, 80);
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            // Audio that is already late is worthless; dropping it keeps latency bounded.
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500),
        };

        var pipeline = BuildPipeline(buffer.ToSampleProvider());

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        lock (_gate)
        {
            _enumerator = enumerator;
            _device = device;
            _capture = capture;
            _buffer = buffer;
            _pipeline = pipeline;
            _framePosition = 0;
        }

        capture.StartRecording();
    }

    public void Stop()
    {
        _stopping = true;

        WasapiCapture? capture;
        MMDevice? device;
        MMDeviceEnumerator? enumerator;

        lock (_gate)
        {
            capture = _capture;
            device = _device;
            enumerator = _enumerator;
            _capture = null;
            _device = null;
            _enumerator = null;

            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
            }
        }

        if (capture is not null)
        {
            // StopRecording waits for the capture callback. That callback must not take
            // `_gate` while raising FrameCaptured, or leave-voice deadlocks this thread.
            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
                // The device may already have gone away.
            }

            try
            {
                capture.Dispose();
            }
            catch (Exception)
            {
                // WASAPI can throw from Dispose after a failed Stop.
            }
        }

        device?.Dispose();
        enumerator?.Dispose();

        lock (_gate)
        {
            _buffer = null;
            _pipeline = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    /// <summary>Downmixes to mono and resamples to 48 kHz if the device is not already there.</summary>
    private static ISampleProvider BuildPipeline(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (source.WaveFormat.Channels > 2)
        {
            source = new MultiplexingSampleProvider([source], 1);
        }

        return source.WaveFormat.SampleRate == AudioFormat.SampleRate
            ? source
            : new WdlResamplingSampleProvider(source, AudioFormat.SampleRate);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        List<short[]>? frames = null;
        Exception? failure = null;

        lock (_gate)
        {
            if (_stopping || _buffer is null || _pipeline is null)
            {
                return;
            }

            try
            {
                _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                frames = DrainFrames();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (failure is not null)
        {
            Failed?.Invoke(this, failure);
            return;
        }

        if (frames is null)
        {
            return;
        }

        foreach (var frame in frames)
        {
            FrameCaptured?.Invoke(this, frame);
        }
    }

    private List<short[]> DrainFrames()
    {
        var frames = new List<short[]>();

        while (true)
        {
            var wanted = AudioFormat.FrameSamples - _framePosition;
            var read = _pipeline!.Read(_scratch, 0, wanted);

            if (read == 0)
            {
                return frames;
            }

            for (var i = 0; i < read; i++)
            {
                var amplified = Math.Clamp(_scratch[i] * Gain, -1f, 1f);
                _frame[_framePosition + i] = (short)(amplified * short.MaxValue);
            }

            _framePosition += read;

            if (_framePosition < AudioFormat.FrameSamples)
            {
                return frames;
            }

            _framePosition = 0;

            // A copy per frame keeps the handler free to hold onto the buffer while the
            // capture thread starts filling the next one.
            frames.Add(_frame.AsSpan().ToArray());
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is { } failure)
        {
            Failed?.Invoke(this, failure);
        }
    }
}
