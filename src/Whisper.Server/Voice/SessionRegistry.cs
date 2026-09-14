using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace Whisper.Server.Voice;

/// <summary>
/// The single source of truth for connected clients. Keyed three ways because each
/// caller arrives with a different handle: the hub has a connection id, the relay has
/// an ssrc, and the UDP handshake has a voice token.
/// </summary>
public sealed class SessionRegistry(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, VoiceSession> _byConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, VoiceSession> _bySsrc = new();
    private readonly ConcurrentDictionary<Guid, VoiceSession> _byToken = new();
    private uint _nextSsrc = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);

    public int Count => _byConnection.Count;

    public IEnumerable<VoiceSession> Sessions => _byConnection.Values;

    public VoiceSession Add(string connectionId, Guid clientId, string displayName)
    {
        var session = new VoiceSession(
            connectionId,
            clientId,
            NextSsrc(),
            Guid.NewGuid(),
            displayName,
            timeProvider.GetUtcNow().UtcTicks);

        _byConnection[connectionId] = session;
        _bySsrc[session.Ssrc] = session;
        _byToken[session.VoiceToken] = session;
        return session;
    }

    public bool TryGetByConnection(string connectionId, out VoiceSession session) =>
        _byConnection.TryGetValue(connectionId, out session!);

    public bool TryGetBySsrc(uint ssrc, out VoiceSession session) =>
        _bySsrc.TryGetValue(ssrc, out session!);

    /// <summary>
    /// Completes the UDP handshake by pinning the session to the endpoint the datagram
    /// came from. Later audio packets must match this endpoint exactly.
    /// </summary>
    public bool TryBindEndpoint(Guid voiceToken, IPEndPoint remoteEndPoint, out VoiceSession session)
    {
        if (!_byToken.TryGetValue(voiceToken, out session!))
        {
            return false;
        }

        session.RemoteEndPoint = remoteEndPoint;
        session.Touch(timeProvider.GetUtcNow().UtcTicks);
        return true;
    }

    public void Touch(VoiceSession session) => session.Touch(timeProvider.GetUtcNow().UtcTicks);

    public IEnumerable<VoiceSession> InVoiceChannel(int channelId) =>
        _byConnection.Values.Where(s => s.VoiceChannelId == channelId);

    public VoiceSession? Remove(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var session))
        {
            return null;
        }

        _bySsrc.TryRemove(session.Ssrc, out _);
        _byToken.TryRemove(session.VoiceToken, out _);
        return session;
    }

    /// <summary>
    /// Drops the UDP binding of any session that has stopped sending keepalives. The chat
    /// session survives: SignalR manages that lifetime and may still be perfectly healthy.
    /// </summary>
    public IReadOnlyList<VoiceSession> EvictExpiredVoiceBindings(TimeSpan timeout)
    {
        var cutoff = timeProvider.GetUtcNow().UtcTicks - timeout.Ticks;
        List<VoiceSession>? evicted = null;

        foreach (var session in _byConnection.Values)
        {
            if (session.RemoteEndPoint is null || session.LastSeenTicks > cutoff)
            {
                continue;
            }

            session.ClearVoiceBinding();
            (evicted ??= []).Add(session);
        }

        return evicted ?? (IReadOnlyList<VoiceSession>)[];
    }

    private uint NextSsrc()
    {
        while (true)
        {
            var candidate = unchecked(Interlocked.Increment(ref _nextSsrc));
            if (candidate != 0 && !_bySsrc.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }
}
