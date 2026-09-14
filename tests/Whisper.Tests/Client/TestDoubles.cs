using System.IO;
using System.Net;
using Whisper.Client.Audio;
using Whisper.Client.Infrastructure;
using Whisper.Client.Models;
using Whisper.Client.Services;
using Whisper.Shared.Contracts;

namespace Whisper.Tests.Client;

/// <summary>Runs posted work immediately, which keeps view model tests synchronous.</summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

/// <summary>Runs audio device work inline so voice-session tests need no audio thread.</summary>
internal sealed class ImmediateAudioThread : IAudioThread
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        action();
        return Task.CompletedTask;
    }
}

/// <summary>Reversible stand-in for DPAPI so persistence can be asserted deterministically.</summary>
internal sealed class ReversibleSecretProtector : ISecretProtector
{
    public const string Prefix = "protected:";

    public string Protect(string plainText) => Prefix + Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes(plainText));

    public bool TryUnprotect(string protectedText, out string plainText)
    {
        plainText = string.Empty;

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        plainText = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(protectedText[Prefix.Length..]));

        return true;
    }
}

/// <summary>Models a profiles.json copied from another machine: the ciphertext is intact
/// but undecryptable here.</summary>
internal sealed class FailingSecretProtector : ISecretProtector
{
    public string Protect(string plainText) => "opaque";

    public bool TryUnprotect(string protectedText, out string plainText)
    {
        plainText = string.Empty;
        return false;
    }
}

internal sealed class FakeWhisperConnection : IWhisperConnection
{
    private ClientConnectionState _state = ClientConnectionState.Disconnected;

    public ClientConnectionState State
    {
        get => _state;
        set
        {
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public AuthResult? Session { get; private set; }

    public AuthResult NextAuthResult { get; set; } = AuthResult.Ok(
        "Test Server",
        Guid.NewGuid(),
        ssrc: 7,
        voicePort: 5001,
        channels: [],
        members: []);

    public HistoryPage NextHistoryPage { get; set; } = new(0, [], false);

    public Exception? SendMessageException { get; set; }

    public List<(int ChannelId, string Content)> SentMessages { get; } = [];

    public List<int> JoinedVoiceChannels { get; } = [];

    public List<bool> MuteCalls { get; } = [];

    public List<bool> DeafenCalls { get; } = [];

    public List<(int ChannelId, DateTimeOffset? Before, int Take)> HistoryRequests { get; } = [];

    public int LeaveVoiceCalls { get; private set; }

    public int DisconnectCalls { get; private set; }

    public ServerProfile? LastProfile { get; private set; }

    public Guid LastClientId { get; private set; }

    public event EventHandler<ClientConnectionState>? StateChanged;

    public event EventHandler<ChatMessage>? MessageReceived;

    public event EventHandler<MemberInfo>? MemberJoined;

    public event EventHandler<Guid>? MemberLeft;

    public event EventHandler<MemberInfo>? MemberUpdated;

    public event EventHandler<SpeakingChange>? SpeakingChanged;

    public event EventHandler<IReadOnlyList<ChannelInfo>>? ChannelsUpdated;

    public event EventHandler<string>? ServerNotice;

    public Task<AuthResult> ConnectAsync(
        ServerProfile profile,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        LastProfile = profile;
        LastClientId = clientId;

        if (NextAuthResult.Success)
        {
            Session = NextAuthResult;
            State = ClientConnectionState.Connected;
        }

        return Task.FromResult(NextAuthResult);
    }

    public Task DisconnectAsync()
    {
        DisconnectCalls++;
        Session = null;
        State = ClientConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<ChatMessage> SendMessageAsync(int channelId, string content)
    {
        if (SendMessageException is { } failure)
        {
            return Task.FromException<ChatMessage>(failure);
        }

        SentMessages.Add((channelId, content));

        return Task.FromResult(new ChatMessage(
            SentMessages.Count,
            channelId,
            Guid.Empty,
            "Me",
            content,
            DateTimeOffset.UtcNow));
    }

    public Task<HistoryPage> GetHistoryAsync(int channelId, DateTimeOffset? before, int take)
    {
        HistoryRequests.Add((channelId, before, take));
        return Task.FromResult(NextHistoryPage);
    }

    public Task JoinVoiceAsync(int channelId)
    {
        JoinedVoiceChannels.Add(channelId);
        return Task.CompletedTask;
    }

    public Task LeaveVoiceAsync()
    {
        LeaveVoiceCalls++;
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool isMuted)
    {
        MuteCalls.Add(isMuted);
        return Task.CompletedTask;
    }

    public Task SetDeafenedAsync(bool isDeafened)
    {
        DeafenCalls.Add(isDeafened);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseMessageReceived(ChatMessage message) => MessageReceived?.Invoke(this, message);

    public void RaiseMemberJoined(MemberInfo member) => MemberJoined?.Invoke(this, member);

    public void RaiseMemberLeft(Guid clientId) => MemberLeft?.Invoke(this, clientId);

    public void RaiseMemberUpdated(MemberInfo member) => MemberUpdated?.Invoke(this, member);

    public void RaiseSpeakingChanged(uint ssrc, bool isSpeaking) =>
        SpeakingChanged?.Invoke(this, new SpeakingChange(ssrc, isSpeaking));

    public void RaiseChannelsUpdated(IReadOnlyList<ChannelInfo> channels) =>
        ChannelsUpdated?.Invoke(this, channels);

    public void RaiseServerNotice(string text) => ServerNotice?.Invoke(this, text);
}

/// <summary>Keeps the profile document in memory so view model tests never touch disk.</summary>
internal sealed class InMemoryProfileStore : IProfileStore
{
    public ProfileDocument Document { get; set; } = new();

    public int SaveCalls { get; private set; }

    public string FilePath => "(memory)";

    public ProfileDocument Load() => Document;

    public void Save(ProfileDocument document)
    {
        Document = document;
        SaveCalls++;
    }
}

/// <summary>
/// Stands in for the whole media plane: records what the view model asked for without
/// opening a socket or an audio device.
/// </summary>
internal sealed class FakeVoiceSession : IVoiceSession
{
    public bool IsActive { get; private set; }

    public bool IsMuted { get; set; }

    public bool IsDeafened { get; set; }

    public bool IsTransmitting { get; private set; }

    public float InputLevel { get; private set; }

    public AudioSettings Settings { get; private set; } = new();

    /// <summary>Set false to simulate UDP being blocked while the hub still works.</summary>
    public bool StartSucceeds { get; set; } = true;

    public List<(IPEndPoint Relay, Guid Token, uint Ssrc)> StartCalls { get; } = [];

    public int StopCalls { get; private set; }

    public event EventHandler<float>? InputLevelChanged;

    public event EventHandler<Exception>? Failed;

    public Task<bool> StartAsync(
        IPEndPoint relayEndPoint,
        Guid voiceToken,
        uint ssrc,
        CancellationToken cancellationToken = default)
    {
        StartCalls.Add((relayEndPoint, voiceToken, ssrc));
        IsActive = StartSucceeds;
        return Task.FromResult(StartSucceeds);
    }

    public Task StopAsync(bool releaseDevices = false)
    {
        StopCalls++;
        IsActive = false;
        return Task.CompletedTask;
    }

    public void ApplySettings(AudioSettings settings) => Settings = settings;

    public void SetPushToTalkPressed(bool isPressed) => IsTransmitting = isPressed && !IsMuted;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseFailed(Exception exception) => Failed?.Invoke(this, exception);

    public void RaiseInputLevel(float level)
    {
        InputLevel = level;
        InputLevelChanged?.Invoke(this, level);
    }
}

/// <summary>Creates and cleans up a temporary profiles directory.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whisper-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
