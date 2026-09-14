using Whisper.Shared;

namespace Whisper.Client.Audio;

public readonly record struct JitterFrame(ushort Sequence, byte[]? Payload)
{
    /// <summary>True when the frame never arrived and the decoder should conceal the gap.</summary>
    public bool IsLost => Payload is null;
}

/// <summary>
/// Absorbs network jitter for one remote speaker. Packets arrive out of order, in bursts,
/// or not at all; playback needs exactly one frame every 20 ms. The buffer trades a small
/// fixed delay for the ability to reorder, and reports gaps so the decoder can conceal
/// them rather than clicking.
/// </summary>
public sealed class JitterBuffer(TimeProvider timeProvider, int targetFrames = 3)
{
    private readonly Dictionary<ushort, byte[]> _frames = [];
    private readonly Lock _gate = new();

    private ushort _nextSequence;
    private bool _primed;
    private long _lastArrivalTicks;

    /// <summary>Frames to collect before playback starts. Three frames is 60 ms of slack.</summary>
    public int TargetFrames { get; set; } = Math.Max(1, targetFrames);

    /// <summary>Hard ceiling, so a flood cannot grow the buffer without bound.</summary>
    public int MaxFrames { get; set; } = 24;

    /// <summary>
    /// Silence longer than this means the speaker stopped talking, so the buffer re-primes
    /// instead of replaying stale frames the moment they resume.
    /// </summary>
    public TimeSpan StreamGap { get; set; } = TimeSpan.FromSeconds(1);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    public bool IsPrimed
    {
        get
        {
            lock (_gate)
            {
                return _primed;
            }
        }
    }

    public int DroppedLateFrames { get; private set; }

    public int DroppedOverflowFrames { get; private set; }

    public int ConcealedFrames { get; private set; }

    /// <summary>Returns false when the frame was too late, a duplicate, or squeezed out.</summary>
    public bool Add(ushort sequence, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow().UtcTicks;

            if (_frames.Count > 0 || _primed)
            {
                if (now - _lastArrivalTicks > StreamGap.Ticks)
                {
                    ResetCore();
                }
            }

            _lastArrivalTicks = now;

            // Before priming, the playout position is not fixed yet, so nothing can be
            // late: initial reordering is simply absorbed.
            if (_primed && SequenceNumber.Distance(sequence, _nextSequence) < 0)
            {
                DroppedLateFrames++;
                return false;
            }

            if (!_frames.TryAdd(sequence, payload.ToArray()))
            {
                return false;
            }

            if (_frames.Count > MaxFrames)
            {
                _frames.Remove(OldestSequence());
                DroppedOverflowFrames++;
            }

            return true;
        }
    }

    /// <summary>
    /// Produces the next frame to play. Returns false while the buffer is filling or if
    /// the speaker has gone quiet, which the mixer renders as silence.
    /// </summary>
    public bool TryDequeue(out JitterFrame frame)
    {
        lock (_gate)
        {
            frame = default;

            if (!_primed)
            {
                if (_frames.Count < TargetFrames)
                {
                    return false;
                }

                _primed = true;
                _nextSequence = OldestSequence();
            }

            if (_frames.Remove(_nextSequence, out var payload))
            {
                frame = new JitterFrame(_nextSequence, payload);
                _nextSequence = SequenceNumber.Next(_nextSequence);
                return true;
            }

            if (_frames.Count == 0)
            {
                // Starved. Re-prime rather than playing single frames as they trickle in.
                _primed = false;
                return false;
            }

            frame = new JitterFrame(_nextSequence, null);
            _nextSequence = SequenceNumber.Next(_nextSequence);
            ConcealedFrames++;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _frames.Clear();
        _primed = false;
    }

    /// <summary>
    /// Oldest buffered sequence under modular comparison, so the wrap from 65535 to 0 does
    /// not look like a jump backwards.
    /// </summary>
    private ushort OldestSequence()
    {
        var oldest = ushort.MaxValue;
        var first = true;

        foreach (var sequence in _frames.Keys)
        {
            if (first || SequenceNumber.Distance(sequence, oldest) < 0)
            {
                oldest = sequence;
                first = false;
            }
        }

        return oldest;
    }
}
