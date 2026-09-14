using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.Shared;

namespace Whisper.Client.Audio;

public sealed class NAudioOutput : IAudioOutput
{
    private readonly Lock _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiOut? _output;
    private FrameSourceWaveProvider? _provider;
    private bool _disposed;

    public bool IsPlaying => _output is not null;

    public event EventHandler<Exception>? Failed;

    public void Start(string? deviceId, IFrameSource source)
    {
        Stop();

        var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = NAudioDeviceProvider.Resolve(enumerator, deviceId, DataFlow.Render)
                ?? throw new InvalidOperationException("No playback device is available.");
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }

        var provider = new FrameSourceWaveProvider(source);

        // Event-sync WASAPI uses a dedicated callback thread that is awkward to tear down
        // from another thread. Shared-mode polling is a few milliseconds more latency and
        // Stop() actually returns.
        var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 80);
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(provider);

        lock (_gate)
        {
            _enumerator = enumerator;
            _device = device;
            _provider = provider;
            _output = output;
        }

        output.Play();
    }

    public void Stop()
    {
        WasapiOut? output;
        FrameSourceWaveProvider? provider;
        MMDevice? device;
        MMDeviceEnumerator? enumerator;

        lock (_gate)
        {
            output = _output;
            provider = _provider;
            device = _device;
            enumerator = _enumerator;
            _output = null;
            _provider = null;
            _device = null;
            _enumerator = null;
        }

        // Detach the mixer before touching the device: in-flight Read() calls finish
        // without walking disposed Opus decoders, which is a native crash.
        provider?.BeginStop();

        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;

            var stop = Task.Run(() =>
            {
                try
                {
                    output.Stop();
                }
                catch (Exception)
                {
                    // Device already gone.
                }
            });

            if (stop.Wait(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    output.Dispose();
                }
                catch (Exception)
                {
                    // WASAPI can throw from Dispose after a failed Stop.
                }
            }
            else
            {
                Failed?.Invoke(this, new TimeoutException("Playback did not stop in time."));
            }
        }

        device?.Dispose();
        enumerator?.Dispose();
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

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is { } failure)
        {
            Failed?.Invoke(this, failure);
        }
    }

    /// <summary>Adapts the mixer to the byte-oriented provider NAudio wants to pull from.</summary>
    private sealed class FrameSourceWaveProvider(IFrameSource source) : IWaveProvider
    {
        private readonly Lock _read = new();
        private float[] _scratch = [];
        private bool _stopping;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels);

        public void BeginStop()
        {
            _stopping = true;

            // Wait for the playback thread to leave source.Read before the mixer is cleared.
            lock (_read)
            {
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var samples = count / sizeof(float);

            if (_scratch.Length < samples)
            {
                _scratch = new float[samples];
            }

            var span = _scratch.AsSpan(0, samples);

            lock (_read)
            {
                if (_stopping)
                {
                    span.Clear();
                }
                else
                {
                    source.Read(span);
                }
            }

            // Never return 0: WASAPI treats a short read as end of stream and stops.
            MemoryMarshal.AsBytes(span).CopyTo(buffer.AsSpan(offset, samples * sizeof(float)));
            return samples * sizeof(float);
        }
    }
}
