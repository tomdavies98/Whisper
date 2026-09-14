using Whisper.Shared.Contracts;

namespace Whisper.Server.Data;

public sealed class ChannelEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ChannelKind Kind { get; set; }

    public int Position { get; set; }

    public ChannelInfo ToInfo() => new(Id, Name, Kind, Position);
}

public sealed class MessageEntity
{
    public long Id { get; set; }

    public int ChannelId { get; set; }

    public Guid SenderClientId { get; set; }

    /// <summary>
    /// Denormalised on purpose: history keeps the name the author used when they sent it,
    /// so a later rename does not rewrite the past.
    /// </summary>
    public string SenderName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>UTC ticks. Stored as an integer so ordering and range filters are exact in SQLite.</summary>
    public long SentUtcTicks { get; set; }

    public ChatMessage ToContract() => new(
        Id,
        ChannelId,
        SenderClientId,
        SenderName,
        Content,
        new DateTimeOffset(SentUtcTicks, TimeSpan.Zero));
}

public sealed class ServerSettingsEntity
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string ServerName { get; set; } = string.Empty;

    public byte[] PasswordHash { get; set; } = [];

    public byte[] PasswordSalt { get; set; } = [];

    public int MaxMembers { get; set; }
}

public sealed class KnownMemberEntity
{
    public Guid ClientId { get; set; }

    public string LastDisplayName { get; set; } = string.Empty;

    public long LastSeenUtcTicks { get; set; }
}
