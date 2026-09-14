using Whisper.Shared.Contracts;

namespace Whisper.Client.ViewModels;

public sealed class ChatMessageViewModel(ChatMessage message)
{
    public long Id { get; } = message.Id;

    public int ChannelId { get; } = message.ChannelId;

    public string SenderName { get; } = message.SenderName;

    public string Content { get; } = message.Content;

    public DateTimeOffset SentLocal { get; } = message.SentUtc.ToLocalTime();

    public string TimeLabel => SentLocal.ToString("HH:mm");

    public string DayLabel => SentLocal.ToString("d MMM yyyy");
}
