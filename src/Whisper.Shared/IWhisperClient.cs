using Whisper.Shared.Contracts;

namespace Whisper.Shared;

/// <summary>Callbacks the server pushes to connected clients.</summary>
public interface IWhisperClient
{
    Task MessageReceived(ChatMessage message);

    Task MemberJoined(MemberInfo member);

    Task MemberLeft(Guid clientId);

    Task MemberUpdated(MemberInfo member);

    Task SpeakingChanged(uint ssrc, bool isSpeaking);

    Task ChannelsUpdated(IReadOnlyList<ChannelInfo> channels);

    Task ServerNotice(string text);
}
