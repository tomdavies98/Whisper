using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Whisper.Client.Audio;
using Whisper.Client.Infrastructure;
using Whisper.Client.Models;
using Whisper.Client.Services;
using Whisper.Shared;
using Whisper.Shared.Contracts;

namespace Whisper.Client.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IWhisperConnection _connection;
    private readonly IUiDispatcher _ui;
    private readonly IVoiceSession _voice;
    private readonly IProfileStore _profileStore;
    private readonly PushToTalkMonitor _pushToTalk;

    private ServerProfile? _profile;
    private AuthResult? _session;
    private Guid _clientId;

    /// <summary>
    /// Selecting a channel starts a history load, but a property setter cannot be awaited.
    /// Holding the task lets <see cref="AttachAsync"/> wait for the first page instead of
    /// racing it with a second request.
    /// </summary>
    private Task _pendingHistoryLoad = Task.CompletedTask;

    [ObservableProperty]
    private string _serverName = string.Empty;

    [ObservableProperty]
    private ChannelInfo? _selectedTextChannel;

    [ObservableProperty]
    private int? _activeVoiceChannelId;

    [ObservableProperty]
    private string _messageDraft = string.Empty;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isDeafened;

    [ObservableProperty]
    private bool _hasMoreHistory;

    [ObservableProperty]
    private bool _isLoadingHistory;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ClientConnectionState _connectionState = ClientConnectionState.Disconnected;

    public MainViewModel(
        IWhisperConnection connection,
        IUiDispatcher ui,
        IVoiceSession voice,
        IProfileStore profileStore,
        PushToTalkMonitor pushToTalk)
    {
        _connection = connection;
        _ui = ui;
        _voice = voice;
        _profileStore = profileStore;
        _pushToTalk = pushToTalk;

        _pushToTalk.PressedChanged += (_, pressed) => _voice.SetPushToTalkPressed(pressed);
        _voice.Failed += (_, ex) => _ui.Post(() => StatusMessage = $"Audio problem: {ex.Message}");

        _connection.MessageReceived += OnMessageReceived;
        _connection.MemberJoined += OnMemberJoined;
        _connection.MemberLeft += OnMemberLeft;
        _connection.MemberUpdated += OnMemberUpdated;
        _connection.SpeakingChanged += OnSpeakingChanged;
        _connection.ChannelsUpdated += OnChannelsUpdated;
        _connection.ServerNotice += OnServerNotice;
        _connection.StateChanged += OnStateChanged;
    }

    public ObservableCollection<ChannelInfo> TextChannels { get; } = [];

    public ObservableCollection<VoiceChannelViewModel> VoiceChannels { get; } = [];

    public ObservableCollection<MemberViewModel> Members { get; } = [];

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public event EventHandler? Disconnected;

    public event EventHandler? SettingsRequested;

    /// <summary>Called once authentication succeeds, with the session the server returned.</summary>
    public async Task AttachAsync(ServerProfile profile, AuthResult session)
    {
        _profile = profile;
        _session = session;
        _clientId = _profileStore.Load().ClientId;
        ServerName = session.ServerName;
        ConnectionState = _connection.State;

        _voice.ApplySettings(_profileStore.Load().Audio);

        ApplyChannels(session.Channels);

        Members.Clear();
        foreach (var member in session.Members)
        {
            Members.Add(new MemberViewModel(member));
        }

        RebuildVoiceOccupancy();

        Messages.Clear();
        SelectedTextChannel = TextChannels.FirstOrDefault();

        await _pendingHistoryLoad;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var content = MessageDraft.Trim();

        if (content.Length == 0 || SelectedTextChannel is not { } channel)
        {
            return;
        }

        if (content.Length > ProtocolLimits.MaxMessageLength)
        {
            StatusMessage = $"Messages are limited to {ProtocolLimits.MaxMessageLength} characters.";
            return;
        }

        // Cleared up front so the textbox feels responsive; restored if the send fails.
        MessageDraft = string.Empty;

        try
        {
            await _connection.SendMessageAsync(channel.Id, content);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            MessageDraft = content;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task LoadOlderAsync()
    {
        if (!HasMoreHistory || IsLoadingHistory)
        {
            return;
        }

        var oldest = Messages.FirstOrDefault();
        await LoadHistoryAsync(oldest?.SentLocal.ToUniversalTime());
    }

    [RelayCommand]
    private async Task JoinVoiceAsync(ChannelInfo? channel)
    {
        if (channel is null || channel.Kind != ChannelKind.Voice)
        {
            return;
        }

        if (channel.Id == ActiveVoiceChannelId && _voice.IsActive)
        {
            return;
        }

        try
        {
            await _connection.JoinVoiceAsync(channel.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        StatusMessage = $"Joining {channel.Name}...";

        if (!await StartVoiceAsync())
        {
            // Chat still works, so back out of the voice channel rather than leaving the
            // member list claiming they are in it.
            await LeaveVoiceAsync();
            return;
        }

        ActiveVoiceChannelId = channel.Id;
        ReflectOwnVoiceChannel(channel.Id);
        StatusMessage = $"Joined {channel.Name}.";
    }

    [RelayCommand]
    private async Task LeaveVoiceAsync()
    {
        _pushToTalk.Stop();

        try
        {
            await _voice.StopAsync(releaseDevices: false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not leave voice cleanly: {ex.Message}";
        }

        try
        {
            await _connection.LeaveVoiceAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        ActiveVoiceChannelId = null;
        ReflectOwnVoiceChannel(null);
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        IsMuted = !IsMuted;
        _voice.IsMuted = IsMuted;

        try
        {
            await _connection.SetMutedAsync(IsMuted);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        if (FindMember(_clientId) is { } self)
        {
            self.IsMuted = IsMuted;
        }
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Brings up the media plane. Returns false when voice is unavailable, which is a
    /// recoverable state: a server may be reachable on TCP while UDP is blocked.
    /// </summary>
    private async Task<bool> StartVoiceAsync()
    {
        if (_voice.IsActive)
        {
            return true;
        }

        if (_profile is null || _session is null)
        {
            return false;
        }

        IPEndPoint relay;

        try
        {
            relay = await ResolveRelayAsync(_profile.Host, _session.VoicePort);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not resolve the voice relay address: {ex.Message}";
            return false;
        }

        var started = await _voice.StartAsync(relay, _session.VoiceToken, _session.Ssrc);

        if (!started)
        {
            StatusMessage =
                $"Voice could not connect. Check that UDP {_session.VoicePort} reaches the server.";
            return false;
        }

        _voice.IsMuted = IsMuted;
        _voice.IsDeafened = IsDeafened;

        if (_voice.Settings.UsePushToTalk)
        {
            _pushToTalk.VirtualKey = _voice.Settings.PushToTalkKey;
            _pushToTalk.Start();
        }

        return true;
    }

    private static async Task<IPEndPoint> ResolveRelayAsync(string host, int port)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return new IPEndPoint(address, port);
        }

        var resolved = await Dns.GetHostAddressesAsync(host);

        return new IPEndPoint(
            resolved.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?? resolved[0],
            port);
    }

    [RelayCommand]
    private async Task ToggleDeafenAsync()
    {
        IsDeafened = !IsDeafened;
        _voice.IsDeafened = IsDeafened;

        // Deafening implies muting: hearing nobody while still transmitting is never
        // what the user meant.
        if (IsDeafened && !IsMuted)
        {
            IsMuted = true;
            _voice.IsMuted = true;
            await SafeInvoke(() => _connection.SetMutedAsync(true));
        }

        await SafeInvoke(() => _connection.SetDeafenedAsync(IsDeafened));

        if (FindMember(_clientId) is { } self)
        {
            self.IsMuted = IsMuted;
            self.IsDeafened = IsDeafened;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _pushToTalk.Stop();

        try
        {
            await _voice.StopAsync(releaseDevices: true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        await _connection.DisconnectAsync();
        ActiveVoiceChannelId = null;
        ReflectOwnVoiceChannel(null);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadHistoryAsync(DateTimeOffset? before)
    {
        if (SelectedTextChannel is not { } channel)
        {
            return;
        }

        IsLoadingHistory = true;

        try
        {
            var page = await _connection.GetHistoryAsync(
                channel.Id,
                before,
                ProtocolLimits.DefaultHistoryPageSize);

            // The server returns newest first and the transcript reads oldest to newest,
            // so walking the page in order and always inserting at the front both reverses
            // it and prepends it ahead of anything already on screen.
            foreach (var message in page.Messages)
            {
                Messages.Insert(0, new ChatMessageViewModel(message));
            }

            HasMoreHistory = page.HasMore;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    private void ApplyChannels(IReadOnlyList<ChannelInfo> channels)
    {
        TextChannels.Clear();
        VoiceChannels.Clear();

        foreach (var channel in channels.OrderBy(c => c.Position))
        {
            if (channel.Kind == ChannelKind.Text)
            {
                TextChannels.Add(channel);
            }
            else
            {
                VoiceChannels.Add(new VoiceChannelViewModel(channel));
            }
        }

        RebuildVoiceOccupancy();
    }

    private void OnMessageReceived(object? sender, ChatMessage message) => _ui.Post(() =>
    {
        if (message.ChannelId != SelectedTextChannel?.Id || Messages.Any(m => m.Id == message.Id))
        {
            return;
        }

        Messages.Add(new ChatMessageViewModel(message));
    });

    private void OnMemberJoined(object? sender, MemberInfo member) => _ui.Post(() =>
    {
        if (FindMember(member.ClientId) is { } existing)
        {
            existing.Update(member);
            RebuildVoiceOccupancy();
            return;
        }

        Members.Add(new MemberViewModel(member));
        RebuildVoiceOccupancy();
        StatusMessage = $"{member.DisplayName} joined.";
    });

    private void OnMemberLeft(object? sender, Guid clientId) => _ui.Post(() =>
    {
        if (FindMember(clientId) is not { } member)
        {
            return;
        }

        Members.Remove(member);
        RebuildVoiceOccupancy();
        StatusMessage = $"{member.DisplayName} left.";
    });

    private void OnMemberUpdated(object? sender, MemberInfo member) => _ui.Post(() =>
    {
        if (FindMember(member.ClientId) is { } existing)
        {
            existing.Update(member);
        }
        else
        {
            Members.Add(new MemberViewModel(member));
        }

        RebuildVoiceOccupancy();
    });

    private void OnSpeakingChanged(object? sender, SpeakingChange change) => _ui.Post(() =>
    {
        if (Members.FirstOrDefault(m => m.Ssrc == change.Ssrc) is { } member)
        {
            member.IsSpeaking = change.IsSpeaking;
        }
    });

    private void OnChannelsUpdated(object? sender, IReadOnlyList<ChannelInfo> channels) =>
        _ui.Post(() => ApplyChannels(channels));

    private void OnServerNotice(object? sender, string text) => _ui.Post(() => StatusMessage = text);

    private void OnStateChanged(object? sender, ClientConnectionState state) => _ui.Post(() =>
    {
        ConnectionState = state;

        StatusMessage = state switch
        {
            ClientConnectionState.Reconnecting => "Connection lost. Reconnecting...",
            ClientConnectionState.Connected => string.Empty,
            ClientConnectionState.Disconnected => "Disconnected.",
            _ => StatusMessage,
        };
    });

    private MemberViewModel? FindMember(Guid clientId) =>
        Members.FirstOrDefault(m => m.ClientId == clientId);

    /// <summary>
    /// Puts people under the voice channel they are actually in. Discord's sidebar is a
    /// roster, not a separate window, so occupancy has to stay in lock-step with member
    /// updates rather than being fetched on its own.
    /// </summary>
    private void RebuildVoiceOccupancy()
    {
        foreach (var channel in VoiceChannels)
        {
            channel.IsJoined = channel.Id == ActiveVoiceChannelId;

            var occupants = Members
                .Where(member => member.VoiceChannelId == channel.Id)
                .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            SyncCollection(channel.Occupants, occupants);
        }
    }

    /// <summary>
    /// Mirrors <paramref name="wanted"/> into <paramref name="target"/> without clearing
    /// first, so WPF does not rebuild every row (and lose the speaking animation) on each
    /// mute toggle.
    /// </summary>
    private static void SyncCollection(
        ObservableCollection<MemberViewModel> target,
        IReadOnlyList<MemberViewModel> wanted)
    {
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            var member = wanted[index];
            var current = target.IndexOf(member);

            if (current < 0)
            {
                target.Insert(index, member);
            }
            else if (current != index)
            {
                target.Move(current, index);
            }
        }
    }

    /// <summary>
    /// The hub's member list does not include the connecting client, so occupancy would
    /// otherwise show everyone except you. Reflect join/leave locally; the broadcast that
    /// follows just confirms it.
    /// </summary>
    private void ReflectOwnVoiceChannel(int? channelId)
    {
        if (_profile is null || _session is null)
        {
            RebuildVoiceOccupancy();
            return;
        }

        var self = FindMember(_clientId);

        if (channelId is null)
        {
            if (self is not null)
            {
                self.VoiceChannelId = null;
            }

            RebuildVoiceOccupancy();
            return;
        }

        if (self is null)
        {
            self = new MemberViewModel(new MemberInfo(
                _clientId,
                _profile.DisplayName,
                _session.Ssrc,
                channelId,
                IsMuted,
                IsDeafened));
            Members.Add(self);
        }
        else
        {
            self.VoiceChannelId = channelId;
            self.IsMuted = IsMuted;
            self.IsDeafened = IsDeafened;
        }

        RebuildVoiceOccupancy();
    }

    private async Task SafeInvoke(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedTextChannelChanged(ChannelInfo? value)
    {
        Messages.Clear();
        HasMoreHistory = false;

        if (value is not null)
        {
            _pendingHistoryLoad = LoadHistoryAsync(before: null);
        }
    }
}
