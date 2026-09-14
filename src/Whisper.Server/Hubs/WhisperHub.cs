using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whisper.Server.Data;
using Whisper.Server.RateLimiting;
using Whisper.Server.Security;
using Whisper.Server.Voice;
using Whisper.Shared;
using Whisper.Shared.Contracts;

namespace Whisper.Server.Hubs;

public sealed class WhisperHub(
    SessionRegistry sessions,
    ServerSettingsCache settings,
    IChannelRepository channels,
    IMessageRepository messages,
    WhisperDbContext db,
    IPasswordHasher passwordHasher,
    HubRateLimits rateLimits,
    IOptions<WhisperServerOptions> options,
    TimeProvider timeProvider,
    ILogger<WhisperHub> logger) : Hub<IWhisperClient>, IWhisperHub
{
    private readonly WhisperServerOptions _options = options.Value;

    public async Task<AuthResult> Authenticate(string serverPassword, Guid clientId, string displayName)
    {
        if (sessions.TryGetByConnection(Context.ConnectionId, out _))
        {
            return AuthResult.Failed("This connection is already authenticated.");
        }

        // Keyed by address, not connection: reconnecting is free, so a per-connection
        // budget would be no obstacle at all to guessing the password.
        if (!rateLimits.Auth.TryAcquire(PeerDescription()))
        {
            logger.LogWarning("Throttled authentication attempts from {Peer}.", PeerDescription());
            return AuthResult.Failed("Too many attempts. Wait a moment and try again.");
        }

        if (clientId == Guid.Empty)
        {
            return AuthResult.Failed("A client identity is required.");
        }

        if (!TryNormaliseDisplayName(displayName, out var name))
        {
            return AuthResult.Failed($"Display name must be 1-{ProtocolLimits.MaxDisplayNameLength} characters.");
        }

        var current = settings.Current;
        if (!passwordHasher.Verify(serverPassword ?? string.Empty, current.PasswordHash, current.PasswordSalt))
        {
            logger.LogWarning("Rejected connection from {Peer}: incorrect password.", PeerDescription());
            return AuthResult.Failed("Incorrect server password.");
        }

        if (sessions.Count >= current.MaxMembers)
        {
            return AuthResult.Failed("The server is full.");
        }

        var session = sessions.Add(Context.ConnectionId, clientId, name);
        await RememberMemberAsync(clientId, name).ConfigureAwait(false);

        var channelList = await channels.GetChannelsAsync(Context.ConnectionAborted).ConfigureAwait(false);
        var members = sessions.Sessions
            .Where(s => s.ConnectionId != Context.ConnectionId)
            .Select(s => s.ToMemberInfo())
            .ToList();

        await Clients.Others.MemberJoined(session.ToMemberInfo()).ConfigureAwait(false);

        logger.LogInformation(
            "{DisplayName} connected from {Peer} (ssrc {Ssrc}).",
            name,
            PeerDescription(),
            session.Ssrc);

        return AuthResult.Ok(
            current.ServerName,
            session.VoiceToken,
            session.Ssrc,
            _options.VoicePort,
            channelList,
            members);
    }

    public async Task<IReadOnlyList<ChannelInfo>> GetChannels()
    {
        RequireSession();
        return await channels.GetChannelsAsync(Context.ConnectionAborted).ConfigureAwait(false);
    }

    public async Task<HistoryPage> GetHistory(int channelId, DateTimeOffset? before, int take)
    {
        RequireSession();

        if (await channels.FindAsync(channelId, Context.ConnectionAborted).ConfigureAwait(false) is not { } channel
            || channel.Kind != ChannelKind.Text)
        {
            throw new HubException("Unknown text channel.");
        }

        return await messages
            .GetHistoryAsync(channelId, before, take, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public async Task<ChatMessage> SendMessage(int channelId, string content)
    {
        var session = RequireSession(chargeActionBudget: false);

        var trimmed = (content ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new HubException("Message is empty.");
        }

        if (trimmed.Length > ProtocolLimits.MaxMessageLength)
        {
            throw new HubException($"Messages are limited to {ProtocolLimits.MaxMessageLength} characters.");
        }

        if (!rateLimits.Chat.TryAcquire(Context.ConnectionId))
        {
            throw new HubException("You are sending messages too quickly.");
        }

        if (await channels.FindAsync(channelId, Context.ConnectionAborted).ConfigureAwait(false) is not { } channel
            || channel.Kind != ChannelKind.Text)
        {
            throw new HubException("Unknown text channel.");
        }

        var message = await messages
            .AddAsync(channelId, session.ClientId, session.DisplayName, trimmed, timeProvider.GetUtcNow())
            .ConfigureAwait(false);

        // Broadcast to everyone including the sender, so every client applies the same
        // server-assigned ordering rather than optimistically inserting its own copy.
        await Clients.All.MessageReceived(message).ConfigureAwait(false);
        return message;
    }

    public async Task JoinVoice(int channelId)
    {
        var session = RequireSession();

        if (await channels.FindAsync(channelId, Context.ConnectionAborted).ConfigureAwait(false) is not { } channel
            || channel.Kind != ChannelKind.Voice)
        {
            throw new HubException("Unknown voice channel.");
        }

        session.VoiceChannelId = channelId;
        sessions.Touch(session);
        await Clients.All.MemberUpdated(session.ToMemberInfo()).ConfigureAwait(false);
    }

    public async Task LeaveVoice()
    {
        var session = RequireSession();
        session.ClearVoiceBinding();
        await Clients.All.MemberUpdated(session.ToMemberInfo()).ConfigureAwait(false);
    }

    public async Task SetMuted(bool isMuted)
    {
        var session = RequireSession();
        session.IsMuted = isMuted;
        await Clients.All.MemberUpdated(session.ToMemberInfo()).ConfigureAwait(false);
    }

    public async Task SetDeafened(bool isDeafened)
    {
        var session = RequireSession();
        session.IsDeafened = isDeafened;
        await Clients.All.MemberUpdated(session.ToMemberInfo()).ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        rateLimits.Forget(Context.ConnectionId);

        if (sessions.Remove(Context.ConnectionId) is { } session)
        {
            logger.LogInformation("{DisplayName} disconnected.", session.DisplayName);
            await Clients.Others.MemberLeft(session.ClientId).ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// Every authenticated method funnels through here, which is also where the catch-all
    /// action budget is charged. <paramref name="chargeActionBudget"/> is false for
    /// <see cref="SendMessage"/>, which has its own, stricter budget.
    /// </summary>
    private VoiceSession RequireSession(bool chargeActionBudget = true)
    {
        if (!sessions.TryGetByConnection(Context.ConnectionId, out var session))
        {
            throw new HubException("Not authenticated.");
        }

        if (chargeActionBudget && !rateLimits.Actions.TryAcquire(Context.ConnectionId))
        {
            throw new HubException("Too many requests. Slow down.");
        }

        return session;
    }

    private async Task RememberMemberAsync(Guid clientId, string displayName)
    {
        var nowTicks = timeProvider.GetUtcNow().UtcTicks;
        var existing = await db.KnownMembers.FirstOrDefaultAsync(m => m.ClientId == clientId).ConfigureAwait(false);

        if (existing is null)
        {
            db.KnownMembers.Add(new KnownMemberEntity
            {
                ClientId = clientId,
                LastDisplayName = displayName,
                LastSeenUtcTicks = nowTicks,
            });
        }
        else
        {
            existing.LastDisplayName = displayName;
            existing.LastSeenUtcTicks = nowTicks;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static bool TryNormaliseDisplayName(string? candidate, out string displayName)
    {
        displayName = (candidate ?? string.Empty).Trim();
        return displayName.Length is > 0 and <= ProtocolLimits.MaxDisplayNameLength;
    }

    private string PeerDescription() =>
        Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
