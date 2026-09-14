using Whisper.Shared.Contracts;

namespace Whisper.Shared;

/// <summary>
/// Server methods a client may invoke. Implemented by the hub and mirrored by
/// <see cref="HubMethods"/>, which the client uses for invocation names.
/// </summary>
public interface IWhisperHub
{
    Task<AuthResult> Authenticate(string serverPassword, Guid clientId, string displayName);

    Task<IReadOnlyList<ChannelInfo>> GetChannels();

    Task<HistoryPage> GetHistory(int channelId, DateTimeOffset? before, int take);

    Task<ChatMessage> SendMessage(int channelId, string content);

    Task JoinVoice(int channelId);

    Task LeaveVoice();

    Task SetMuted(bool isMuted);

    Task SetDeafened(bool isDeafened);
}

public static class HubMethods
{
    public const string Path = "/hub";

    public const string Authenticate = nameof(IWhisperHub.Authenticate);
    public const string GetChannels = nameof(IWhisperHub.GetChannels);
    public const string GetHistory = nameof(IWhisperHub.GetHistory);
    public const string SendMessage = nameof(IWhisperHub.SendMessage);
    public const string JoinVoice = nameof(IWhisperHub.JoinVoice);
    public const string LeaveVoice = nameof(IWhisperHub.LeaveVoice);
    public const string SetMuted = nameof(IWhisperHub.SetMuted);
    public const string SetDeafened = nameof(IWhisperHub.SetDeafened);

    public const string MessageReceived = nameof(IWhisperClient.MessageReceived);
    public const string MemberJoined = nameof(IWhisperClient.MemberJoined);
    public const string MemberLeft = nameof(IWhisperClient.MemberLeft);
    public const string MemberUpdated = nameof(IWhisperClient.MemberUpdated);
    public const string SpeakingChanged = nameof(IWhisperClient.SpeakingChanged);
    public const string ChannelsUpdated = nameof(IWhisperClient.ChannelsUpdated);
    public const string ServerNotice = nameof(IWhisperClient.ServerNotice);
}
