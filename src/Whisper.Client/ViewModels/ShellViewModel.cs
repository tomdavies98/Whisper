using CommunityToolkit.Mvvm.ComponentModel;

namespace Whisper.Client.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableObject _currentView;

    public ShellViewModel(ServerBrowserViewModel browser, MainViewModel session, SettingsViewModel settings)
    {
        Browser = browser;
        Session = session;
        Settings = settings;
        _currentView = browser;

        Browser.Connected += async (_, e) =>
        {
            await Session.AttachAsync(e.Profile, e.Session);
            CurrentView = Session;
        };

        Session.Disconnected += (_, _) =>
        {
            Browser.Reload();
            CurrentView = Browser;
        };

        Session.SettingsRequested += (_, _) =>
        {
            Settings.Load();
            CurrentView = Settings;
        };

        Settings.Closed += (_, _) => CurrentView = Session;
    }

    public ServerBrowserViewModel Browser { get; }

    public MainViewModel Session { get; }

    public SettingsViewModel Settings { get; }

    public string Title => ReferenceEquals(CurrentView, Browser) || Session.ServerName.Length == 0
        ? "Whisper"
        : $"Whisper - {Session.ServerName}";

    partial void OnCurrentViewChanged(ObservableObject value) => OnPropertyChanged(nameof(Title));
}
