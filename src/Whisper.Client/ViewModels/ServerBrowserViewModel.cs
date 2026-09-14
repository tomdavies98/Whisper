using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Whisper.Client.Models;
using Whisper.Client.Services;
using Whisper.Shared;
using Whisper.Shared.Contracts;

namespace Whisper.Client.ViewModels;

public sealed partial class ServerBrowserViewModel : ObservableObject
{
    private readonly IProfileStore _profileStore;
    private readonly IWhisperConnection _connection;
    private ProfileDocument _document = new();

    [ObservableProperty]
    private ServerProfileViewModel? _selectedProfile;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isConnecting;

    public ServerBrowserViewModel(IProfileStore profileStore, IWhisperConnection connection)
    {
        _profileStore = profileStore;
        _connection = connection;
        Reload();
    }

    public ObservableCollection<ServerProfileViewModel> Profiles { get; } = [];

    public Guid ClientId => _document.ClientId;

    /// <summary>Raised once a profile has authenticated, carrying the server's session.</summary>
    public event EventHandler<ConnectedEventArgs>? Connected;

    public void Reload()
    {
        _document = _profileStore.Load();
        Profiles.Clear();

        foreach (var profile in _document.Profiles)
        {
            Profiles.Add(new ServerProfileViewModel(profile));
        }

        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ServerProfileViewModel
        {
            Name = $"Server {Profiles.Count + 1}",
            DisplayName = Environment.UserName,
        };

        Profiles.Add(profile);
        SelectedProfile = profile;
        Save();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveProfile()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        Profiles.Remove(profile);
        SelectedProfile = Profiles.FirstOrDefault();
        Save();
    }

    [RelayCommand]
    private void Save()
    {
        _document.Profiles = Profiles.Select(p => p.ToProfile()).ToList();
        _profileStore.Save(_document);
        StatusMessage = "Saved.";
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is not { } selected)
        {
            return;
        }

        var profile = selected.ToProfile();

        if (Validate(profile) is { } problem)
        {
            StatusMessage = problem;
            return;
        }

        IsConnecting = true;
        StatusMessage = $"Connecting to {profile.Host}:{profile.Port}...";

        try
        {
            Save();
            var result = await _connection.ConnectAsync(profile, ClientId, cancellationToken);

            if (!result.Success)
            {
                StatusMessage = result.FailureReason ?? "The server refused the connection.";
                return;
            }

            StatusMessage = $"Connected to {result.ServerName}.";
            Connected?.Invoke(this, new ConnectedEventArgs(profile, result));
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool HasSelection() => SelectedProfile is not null;

    private bool CanConnect() => SelectedProfile is not null && !IsConnecting;

    private static string? Validate(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            return "Enter the server address.";
        }

        if (profile.Port is < 1 or > 65535)
        {
            return "Port must be between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return "Enter the name others will see.";
        }

        return profile.DisplayName.Length > ProtocolLimits.MaxDisplayNameLength
            ? $"Your name must be {ProtocolLimits.MaxDisplayNameLength} characters or fewer."
            : null;
    }

    partial void OnSelectedProfileChanged(ServerProfileViewModel? value)
    {
        RemoveProfileCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectingChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();
}

public sealed class ConnectedEventArgs(ServerProfile profile, AuthResult session) : EventArgs
{
    public ServerProfile Profile { get; } = profile;

    public AuthResult Session { get; } = session;
}
