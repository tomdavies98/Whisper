using System.Collections.Concurrent;

namespace Whisper.Server.Voice;

/// <summary>
/// Turns a stream of audio packets into edge-triggered speaking notifications. Only
/// transitions are reported, so a continuously talking member produces one broadcast at
/// the start and one when they stop - not fifty per second.
/// </summary>
public sealed class SpeakingTracker(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<uint, State> _states = new();

    /// <summary>Records a packet. Returns true only on the silent-to-speaking edge.</summary>
    public bool NotePacket(uint ssrc)
    {
        var state = _states.GetOrAdd(ssrc, _ => new State());
        Volatile.Write(ref state.LastPacketTicks, timeProvider.GetUtcNow().UtcTicks);
        return Interlocked.CompareExchange(ref state.IsSpeaking, 1, 0) == 0;
    }

    public bool IsSpeaking(uint ssrc) =>
        _states.TryGetValue(ssrc, out var state) && Volatile.Read(ref state.IsSpeaking) == 1;

    /// <summary>Returns the ssrcs that have just crossed from speaking to silent.</summary>
    public IReadOnlyList<uint> CollectStopped(TimeSpan decay)
    {
        var cutoff = timeProvider.GetUtcNow().UtcTicks - decay.Ticks;
        List<uint>? stopped = null;

        foreach (var (ssrc, state) in _states)
        {
            if (Volatile.Read(ref state.IsSpeaking) == 0 || Volatile.Read(ref state.LastPacketTicks) > cutoff)
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref state.IsSpeaking, 0, 1) == 1)
            {
                (stopped ??= []).Add(ssrc);
            }
        }

        return stopped ?? (IReadOnlyList<uint>)[];
    }

    public void Forget(uint ssrc) => _states.TryRemove(ssrc, out _);

    private sealed class State
    {
        public long LastPacketTicks;
        public int IsSpeaking;
    }
}
