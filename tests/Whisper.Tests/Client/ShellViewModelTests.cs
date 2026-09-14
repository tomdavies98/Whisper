using FluentAssertions;
using NSubstitute;
using Whisper.Client.Audio;
using Whisper.Client.Infrastructure;
using Whisper.Client.ViewModels;
using Whisper.Shared.Contracts;
using Xunit;

namespace Whisper.Tests.Client;

/// <summary>
/// The shell owns navigation, which is the one place a wiring mistake strands the user on
/// the wrong screen with no way back.
/// </summary>
public class ShellViewModelTests
{
    private readonly FakeWhisperConnection _connection = new();
    private readonly InMemoryProfileStore _profileStore = new();
    private readonly FakeVoiceSession _voice = new();
    private readonly ServerBrowserViewModel _browser;
    private readonly MainViewModel _session;
    private readonly SettingsViewModel _settings;
    private readonly ShellViewModel _shell;

    public ShellViewModelTests()
    {
        var ui = new ImmediateUiDispatcher();

        _browser = new ServerBrowserViewModel(_profileStore, _connection);
        _session = new MainViewModel(_connection, ui, _voice, _profileStore, new PushToTalkMonitor());
        _settings = new SettingsViewModel(
            Substitute.For<IAudioDeviceProvider>(),
            _voice,
            _profileStore,
            ui);

        _shell = new ShellViewModel(_browser, _session, _settings);
    }

    private async Task ConnectAsync()
    {
        _browser.AddProfileCommand.Execute(null);
        _browser.SelectedProfile!.Host = "127.0.0.1";
        _browser.SelectedProfile.DisplayName = "Tom";
        _connection.NextAuthResult = AuthResult.Ok(
            "Test Server",
            Guid.NewGuid(),
            ssrc: 5,
            voicePort: 5001,
            channels: [],
            members: []);

        await _browser.ConnectCommand.ExecuteAsync(null);
    }

    [Fact]
    public void CurrentView_StartsOnTheServerBrowser()
    {
        _shell.CurrentView.Should().BeSameAs(_browser);
    }

    [Fact]
    public async Task Connecting_SwitchesToTheSessionView()
    {
        await ConnectAsync();

        _shell.CurrentView.Should().BeSameAs(_session);
        _session.ServerName.Should().Be("Test Server");
    }

    [Fact]
    public async Task Title_NamesTheConnectedServer()
    {
        await ConnectAsync();

        _shell.Title.Should().Be("Whisper - Test Server");
    }

    [Fact]
    public void Title_OnTheBrowser_IsJustTheAppName()
    {
        _shell.Title.Should().Be("Whisper");
    }

    [Fact]
    public async Task Disconnecting_ReturnsToTheServerBrowser()
    {
        await ConnectAsync();

        await _session.DisconnectCommand.ExecuteAsync(null);

        _shell.CurrentView.Should().BeSameAs(_browser);
    }

    [Fact]
    public async Task Disconnecting_ReloadsTheProfileListSoEditsAreNotLost()
    {
        await ConnectAsync();

        await _session.DisconnectCommand.ExecuteAsync(null);

        _browser.Profiles.Should().ContainSingle().Which.Host.Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task OpeningSettings_SwitchesToTheAudioPage()
    {
        await ConnectAsync();

        _session.OpenSettingsCommand.Execute(null);

        _shell.CurrentView.Should().BeSameAs(_settings);
    }

    [Fact]
    public async Task ClosingSettings_ReturnsToTheSessionRatherThanTheBrowser()
    {
        // Opening settings mid-call must not look like disconnecting.
        await ConnectAsync();
        _session.OpenSettingsCommand.Execute(null);

        _settings.CloseCommand.Execute(null);

        _shell.CurrentView.Should().BeSameAs(_session);
    }
}
