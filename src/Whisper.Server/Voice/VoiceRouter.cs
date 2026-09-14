using System.Net;

namespace Whisper.Server.Voice;

public enum RouteOutcome
{
    /// <summary>At least one destination was produced.</summary>
    Forwarded,

    /// <summary>No session claims this ssrc, so the packet is unattributable.</summary>
    UnknownSsrc,

    /// <summary>The session exists but never completed the UDP handshake.</summary>
    NotBound,

    /// <summary>The datagram arrived from somewhere other than the bound endpoint.</summary>
    EndpointMismatch,

    /// <summary>The sender is not currently in a voice channel.</summary>
    NotInVoiceChannel,

    /// <summary>The sender muted itself, so audio is dropped at the server.</summary>
    SenderMuted,

    /// <summary>Valid packet, but nobody else is listening in that channel.</summary>
    NoRecipients,
}

/// <summary>
/// Decides where a single audio packet goes. Deliberately pure with respect to I/O: it
/// reads session state and fills a caller-owned destination list, which keeps every
/// forwarding rule testable without a socket and keeps the relay loop allocation-free.
/// </summary>
public sealed class VoiceRouter(SessionRegistry sessions)
{
    public RouteOutcome Route(
        uint ssrc,
        IPEndPoint source,
        ICollection<IPEndPoint> destinations,
        out VoiceSession? sender)
    {
        destinations.Clear();
        sender = null;

        if (!sessions.TryGetBySsrc(ssrc, out var session))
        {
            return RouteOutcome.UnknownSsrc;
        }

        if (session.RemoteEndPoint is not { } bound)
        {
            return RouteOutcome.NotBound;
        }

        // Pinning to the handshake endpoint is what stops a third party from claiming
        // someone else's ssrc and injecting audio into their channel.
        if (!bound.Equals(source))
        {
            return RouteOutcome.EndpointMismatch;
        }

        sender = session;

        if (session.VoiceChannelId is not { } channelId)
        {
            return RouteOutcome.NotInVoiceChannel;
        }

        if (session.IsMuted)
        {
            return RouteOutcome.SenderMuted;
        }

        foreach (var peer in sessions.Sessions)
        {
            if (ReferenceEquals(peer, session)
                || peer.VoiceChannelId != channelId
                || peer.IsDeafened
                || peer.RemoteEndPoint is not { } destination)
            {
                continue;
            }

            destinations.Add(destination);
        }

        return destinations.Count == 0 ? RouteOutcome.NoRecipients : RouteOutcome.Forwarded;
    }
}
