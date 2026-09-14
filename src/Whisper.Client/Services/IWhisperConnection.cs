using Whisper.Client.Models;
using Whisper.Shared.Contracts;

namespace Whisper.Client.Services;

public enum ClientConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

public readonly record struct SpeakingChange(uint Ssrc, bool IsSpeaking);

/// <summary>
/// Everything the UI needs from the server, with SignalR kept on the far side of the
/// interface so view models can be driven by raising these events directly in tests.
/// </summary>
public interface IWhisperConnection : IAsyncDisposable
{
    ClientConnectionState State { get; }

    AuthResult? Session { get; }

    event EventHandler<ClientConnectionState>? StateChanged;

    event EventHandler<ChatMessage>? MessageReceived;

    event EventHandler<MemberInfo>? MemberJoined;

    event EventHandler<Guid>? MemberLeft;

    event EventHandler<MemberInfo>? MemberUpdated;

    event EventHandler<SpeakingChange>? SpeakingChanged;

    event EventHandler<IReadOnlyList<ChannelInfo>>? ChannelsUpdated;

    event EventHandler<string>? ServerNotice;

    Task<AuthResult> ConnectAsync(ServerProfile profile, Guid clientId, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    Task<ChatMessage> SendMessageAsync(int channelId, string content);

    Task<HistoryPage> GetHistoryAsync(int channelId, DateTimeOffset? before, int take);

    Task JoinVoiceAsync(int channelId);

    Task LeaveVoiceAsync();

    Task SetMutedAsync(bool isMuted);

    Task SetDeafenedAsync(bool isDeafened);
}
