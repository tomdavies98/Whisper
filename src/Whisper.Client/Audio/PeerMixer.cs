using System.Collections.Concurrent;
using Whisper.Shared;

namespace Whisper.Client.Audio;

/// <summary>
/// Mixes every remote speaker into one stream. Each peer gets its own jitter buffer and
/// its own decoder, because Opus is stateful and interleaving two speakers through one
/// decoder produces garbage.
/// </summary>
public sealed class PeerMixer(Func<IAudioCodec> codecFactory, TimeProvider timeProvider) : IFrameSource, IDisposable
{
    private readonly ConcurrentDictionary<uint, PeerStream> _peers = new();
    private readonly float[] _block = new float[AudioFormat.FrameSamples];
    private readonly short[] _decoded = new short[AudioFormat.FrameSamples];
    private readonly Lock _readGate = new();

    private int _blockOffset = AudioFormat.FrameSamples;

    public int JitterBufferFrames { get; set; } = 3;

    public bool IsDeafened { get; set; }

    public float OutputVolume { get; set; } = 1f;

    public IReadOnlyCollection<uint> ActiveSsrcs => _peers.Keys.ToArray();

    public void Enqueue(uint ssrc, ushort sequence, ReadOnlySpan<byte> payload)
    {
        var peer = _peers.GetOrAdd(ssrc, _ => new PeerStream(codecFactory(), timeProvider, JitterBufferFrames));
        peer.Buffer.TargetFrames = JitterBufferFrames;
        peer.Buffer.Add(sequence, payload);
    }

    public void Remove(uint ssrc)
    {
        if (_peers.TryRemove(ssrc, out var peer))
        {
            peer.Dispose();
        }
    }

    public void Clear()
    {
        // Take the same lock the output device holds while mixing, so a codec cannot be
        // disposed under an in-flight Read() — that was a native crash on leave-voice.
        lock (_readGate)
        {
            foreach (var ssrc in _peers.Keys)
            {
                Remove(ssrc);
            }

            Array.Clear(_block);
            _blockOffset = AudioFormat.FrameSamples;
        }
    }

    public JitterBuffer? BufferFor(uint ssrc) => _peers.TryGetValue(ssrc, out var peer) ? peer.Buffer : null;

    /// <summary>
    /// Called by the output device. Requests are not frame-aligned, so a partially consumed
    /// block is carried across calls.
    /// </summary>
    public void Read(Span<float> destination)
    {
        lock (_readGate)
        {
            var written = 0;

            while (written < destination.Length)
            {
                if (_blockOffset >= AudioFormat.FrameSamples)
                {
                    MixNextBlock();
                    _blockOffset = 0;
                }

                var take = Math.Min(destination.Length - written, AudioFormat.FrameSamples - _blockOffset);
                _block.AsSpan(_blockOffset, take).CopyTo(destination[written..]);
                written += take;
                _blockOffset += take;
            }
        }
    }

    public void Dispose() => Clear();

    private void MixNextBlock()
    {
        Array.Clear(_block);

        if (IsDeafened)
        {
            return;
        }

        foreach (var peer in _peers.Values)
        {
            if (!peer.TryNextFrame(_decoded))
            {
                continue;
            }

            for (var i = 0; i < AudioFormat.FrameSamples; i++)
            {
                _block[i] += _decoded[i] / 32768f;
            }
        }

        if (OutputVolume is 1f)
        {
            // Summing several speakers can exceed full scale; clamping is a cheap limiter
            // that is far less unpleasant than the wraparound distortion of an overflow.
            for (var i = 0; i < _block.Length; i++)
            {
                _block[i] = Math.Clamp(_block[i], -1f, 1f);
            }

            return;
        }

        for (var i = 0; i < _block.Length; i++)
        {
            _block[i] = Math.Clamp(_block[i] * OutputVolume, -1f, 1f);
        }
    }

    private sealed class PeerStream(IAudioCodec codec, TimeProvider timeProvider, int targetFrames) : IDisposable
    {
        public JitterBuffer Buffer { get; } = new(timeProvider, targetFrames);

        public bool TryNextFrame(short[] destination)
        {
            if (!Buffer.TryDequeue(out var frame))
            {
                return false;
            }

            try
            {
                if (frame.IsLost)
                {
                    codec.DecodeLost(destination);
                }
                else
                {
                    codec.Decode(frame.Payload, destination);
                }

                return true;
            }
            catch (Exception)
            {
                // A corrupt payload must not take down the audio thread; skip the frame.
                return false;
            }
        }

        public void Dispose() => codec.Dispose();
    }
}
