using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Whisper.Shared;
using Whisper.Shared.Contracts;

namespace Whisper.IntegrationTests;

/// <summary>
/// Buffers every server push so a test can await the specific callback it cares about
/// without racing the connection. Each stream is unbounded: dropping an event would turn
/// an ordering bug into a timeout that is much harder to read.
/// </summary>
public sealed class TestClientEvents
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Channel<ChatMessage> _messages = Channel.CreateUnbounded<ChatMessage>();
    private readonly Channel<MemberInfo> _joined = Channel.CreateUnbounded<MemberInfo>();
    private readonly Channel<Guid> _left = Channel.CreateUnbounded<Guid>();
    private readonly Channel<MemberInfo> _updated = Channel.CreateUnbounded<MemberInfo>();
    private readonly Channel<(uint Ssrc, bool IsSpeaking)> _speaking = Channel.CreateUnbounded<(uint, bool)>();
    private readonly Channel<IReadOnlyList<ChannelInfo>> _channels = Channel.CreateUnbounded<IReadOnlyList<ChannelInfo>>();
    private readonly Channel<string> _notices = Channel.CreateUnbounded<string>();

    public TestClientEvents(HubConnection connection)
    {
        connection.On<ChatMessage>(HubMethods.MessageReceived, m => _messages.Writer.TryWrite(m));
        connection.On<MemberInfo>(HubMethods.MemberJoined, m => _joined.Writer.TryWrite(m));
        connection.On<Guid>(HubMethods.MemberLeft, id => _left.Writer.TryWrite(id));
        connection.On<MemberInfo>(HubMethods.MemberUpdated, m => _updated.Writer.TryWrite(m));
        connection.On<uint, bool>(HubMethods.SpeakingChanged, (ssrc, speaking) => _speaking.Writer.TryWrite((ssrc, speaking)));
        connection.On<IReadOnlyList<ChannelInfo>>(HubMethods.ChannelsUpdated, c => _channels.Writer.TryWrite(c));
        connection.On<string>(HubMethods.ServerNotice, text => _notices.Writer.TryWrite(text));
    }

    public AuthResult? AuthResult { get; set; }

    public Task<ChatMessage> NextMessageAsync(TimeSpan? timeout = null) => Read(_messages, timeout);

    public Task<MemberInfo> NextMemberJoinedAsync(TimeSpan? timeout = null) => Read(_joined, timeout);

    public Task<Guid> NextMemberLeftAsync(TimeSpan? timeout = null) => Read(_left, timeout);

    public Task<MemberInfo> NextMemberUpdatedAsync(TimeSpan? timeout = null) => Read(_updated, timeout);

    public Task<(uint Ssrc, bool IsSpeaking)> NextSpeakingChangeAsync(TimeSpan? timeout = null) =>
        Read(_speaking, timeout);

    public Task<IReadOnlyList<ChannelInfo>> NextChannelsUpdateAsync(TimeSpan? timeout = null) =>
        Read(_channels, timeout);

    public Task<string> NextNoticeAsync(TimeSpan? timeout = null) => Read(_notices, timeout);

    public bool HasPendingMessages => _messages.Reader.Count > 0;

    private static async Task<T> Read<T>(Channel<T> channel, TimeSpan? timeout)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);

        try
        {
            return await channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No {typeof(T).Name} callback arrived within {timeout ?? DefaultTimeout}.");
        }
    }
}
