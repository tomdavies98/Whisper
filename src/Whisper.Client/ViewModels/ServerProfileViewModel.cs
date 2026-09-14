using CommunityToolkit.Mvvm.ComponentModel;
using Whisper.Client.Models;

namespace Whisper.Client.ViewModels;

/// <summary>Editable view of a saved server. Kept separate from the persisted model so the
/// list refreshes as the user types without mutating what is on disk.</summary>
public sealed partial class ServerProfileViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "New server";

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = Whisper.Shared.ProtocolLimits.DefaultHubPort;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _rememberPassword;

    [ObservableProperty]
    private bool _useTls;

    [ObservableProperty]
    private bool _allowSelfSignedCertificate;

    public ServerProfileViewModel()
    {
    }

    public ServerProfileViewModel(ServerProfile profile)
    {
        Id = profile.Id;
        _name = profile.Name;
        _host = profile.Host;
        _port = profile.Port;
        _displayName = profile.DisplayName;
        _password = profile.Password;
        _rememberPassword = profile.RememberPassword;
        _useTls = profile.UseTls;
        _allowSelfSignedCertificate = profile.AllowSelfSignedCertificate;
    }

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Summary => $"{Host}:{Port}";

    public ServerProfile ToProfile() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? Host : Name.Trim(),
        Host = Host.Trim(),
        Port = Port,
        DisplayName = DisplayName.Trim(),
        Password = Password,
        RememberPassword = RememberPassword,
        UseTls = UseTls,
        AllowSelfSignedCertificate = AllowSelfSignedCertificate,
    };

    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(Summary));

    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(Summary));
}
