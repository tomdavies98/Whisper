using System.Net;
using Whisper.Shared.Contracts;

namespace Whisper.Server.Voice;

/// <summary>
/// One connected client, shared between the hub (which owns its lifetime) and the UDP
/// relay (which reads it on the packet hot path). Mutable fields are written by the hub
/// thread and read by the relay loop, so each one is either a reference or a 32-bit
/// value with an atomic read - no lock is taken while forwarding audio.
/// </summary>
public sealed class VoiceSession
{
    private const int NoChannel = -1;

    private volatile IPEndPoint? _remoteEndPoint;
    private volatile string _displayName;
    private int _voiceChannelId = NoChannel;
    private long _lastSeenTicks;
    private int _isMuted;
    private int _isDeafened;

    public VoiceSession(string connectionId, Guid clientId, uint ssrc, Guid voiceToken, string displayName, long nowTicks)
    {
        ConnectionId = connectionId;
        ClientId = clientId;
        Ssrc = ssrc;
        VoiceToken = voiceToken;
        _displayName = displayName;
        _lastSeenTicks = nowTicks;
    }

    public string ConnectionId { get; }

    public Guid ClientId { get; }

    public uint Ssrc { get; }

    public Guid VoiceToken { get; }

    public string DisplayName
    {
        get => _displayName;
        set => _displayName = value;
    }

    /// <summary>Set only once the client has completed the UDP handshake from this exact endpoint.</summary>
    public IPEndPoint? RemoteEndPoint
    {
        get => _remoteEndPoint;
        set => _remoteEndPoint = value;
    }

    public int? VoiceChannelId
    {
        get
        {
            var value = Volatile.Read(ref _voiceChannelId);
            return value == NoChannel ? null : value;
        }
        set => Volatile.Write(ref _voiceChannelId, value ?? NoChannel);
    }

    public bool IsMuted
    {
        get => Volatile.Read(ref _isMuted) == 1;
        set => Volatile.Write(ref _isMuted, value ? 1 : 0);
    }

    public bool IsDeafened
    {
        get => Volatile.Read(ref _isDeafened) == 1;
        set => Volatile.Write(ref _isDeafened, value ? 1 : 0);
    }

    public long LastSeenTicks => Volatile.Read(ref _lastSeenTicks);

    public void Touch(long nowTicks) => Volatile.Write(ref _lastSeenTicks, nowTicks);

    /// <summary>Drops the UDP binding and voice membership, leaving the chat session intact.</summary>
    public void ClearVoiceBinding()
    {
        RemoteEndPoint = null;
        VoiceChannelId = null;
    }

    public MemberInfo ToMemberInfo() =>
        new(ClientId, DisplayName, Ssrc, VoiceChannelId, IsMuted, IsDeafened);
}
