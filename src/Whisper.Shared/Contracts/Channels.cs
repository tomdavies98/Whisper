namespace Whisper.Shared.Contracts;

public enum ChannelKind
{
    Text = 0,
    Voice = 1,
}

public sealed record ChannelInfo(int Id, string Name, ChannelKind Kind, int Position);

public sealed record MemberInfo(
    Guid ClientId,
    string DisplayName,
    uint Ssrc,
    int? VoiceChannelId,
    bool IsMuted,
    bool IsDeafened);

public sealed record ChatMessage(
    long Id,
    int ChannelId,
    Guid SenderClientId,
    string SenderName,
    string Content,
    DateTimeOffset SentUtc);

public sealed record HistoryPage(int ChannelId, IReadOnlyList<ChatMessage> Messages, bool HasMore);
