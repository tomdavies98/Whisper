using FluentAssertions;
using Whisper.Client.Models;
using Whisper.Client.Services;
using Whisper.Client.ViewModels;
using Whisper.Shared.Contracts;
using Xunit;

namespace Whisper.Tests.Client;

public class ServerBrowserViewModelTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FakeWhisperConnection _connection = new();
    private readonly ProfileStore _store;

    public ServerBrowserViewModelTests() =>
        _store = new ProfileStore(new ReversibleSecretProtector(), _directory.Path);

    public void Dispose() => _directory.Dispose();

    private ServerBrowserViewModel CreateViewModel() => new(_store, _connection);

    private ServerBrowserViewModel WithOneValidProfile()
    {
        var viewModel = CreateViewModel();
        viewModel.AddProfileCommand.Execute(null);
        viewModel.SelectedProfile!.Host = "192.168.1.20";
        viewModel.SelectedProfile.Port = 5000;
        viewModel.SelectedProfile.DisplayName = "Tom";
        viewModel.SelectedProfile.Password = "hunter2";
        return viewModel;
    }

    [Fact]
    public void Constructor_LoadsSavedProfilesAndSelectsTheFirst()
    {
        _store.Save(new ProfileDocument
        {
            Profiles =
            [
                new ServerProfile { Name = "First", Host = "10.0.0.1" },
                new ServerProfile { Name = "Second", Host = "10.0.0.2" },
            ],
        });

        var viewModel = CreateViewModel();

        viewModel.Profiles.Select(p => p.Name).Should().Equal("First", "Second");
        viewModel.SelectedProfile!.Name.Should().Be("First");
    }

    [Fact]
    public void AddProfile_AddsSelectsAndPersists()
    {
        var viewModel = CreateViewModel();

        viewModel.AddProfileCommand.Execute(null);

        viewModel.Profiles.Should().ContainSingle();
        viewModel.SelectedProfile.Should().BeSameAs(viewModel.Profiles[0]);
        _store.Load().Profiles.Should().ContainSingle();
    }

    [Fact]
    public void RemoveProfile_WithNothingSelected_CannotExecute()
    {
        var viewModel = CreateViewModel();

        viewModel.RemoveProfileCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void RemoveProfile_DeletesTheSelectionAndPersists()
    {
        var viewModel = CreateViewModel();
        viewModel.AddProfileCommand.Execute(null);
        viewModel.AddProfileCommand.Execute(null);

        viewModel.RemoveProfileCommand.Execute(null);

        viewModel.Profiles.Should().ContainSingle();
        _store.Load().Profiles.Should().ContainSingle();
    }

    [Fact]
    public async Task Connect_WithNoAddress_ReportsTheProblemAndDoesNotDial()
    {
        var viewModel = WithOneValidProfile();
        viewModel.SelectedProfile!.Host = "   ";

        await viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Be("Enter the server address.");
        _connection.LastProfile.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task Connect_WithAnImpossiblePort_ReportsTheProblem(int port)
    {
        var viewModel = WithOneValidProfile();
        viewModel.SelectedProfile!.Port = port;

        await viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Be("Port must be between 1 and 65535.");
        _connection.LastProfile.Should().BeNull();
    }

    [Fact]
    public async Task Connect_WithNoDisplayName_ReportsTheProblem()
    {
        var viewModel = WithOneValidProfile();
        viewModel.SelectedProfile!.DisplayName = string.Empty;

        await viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Be("Enter the name others will see.");
        _connection.LastProfile.Should().BeNull();
    }

    [Fact]
    public async Task Connect_WithAnOverlongDisplayName_ReportsTheProblem()
    {
        var viewModel = WithOneValidProfile();
        viewModel.SelectedProfile!.DisplayName = new string('x', 40);

        await viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Contain("32 characters or fewer");
        _connection.LastProfile.Should().BeNull();
    }

    [Fact]
    public async Task Connect_WhenTheServerRejects_SurfacesTheServerReason()
    {
        var viewModel = WithOneValidProfile();
        _connection.NextAuthResult = AuthResult.Failed("Incorrect server password.");
        var raised = false;
        viewModel.Connected += (_, _) => raised = true;

        await viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Be("Incorrect server password.");
        raised.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_OnSuccess_RaisesConnectedWithTheSessionAndProfile()
    {
        var viewModel = WithOneValidProfile();
        ConnectedEventArgs? captured = null;
        viewModel.Connected += (_, e) => captured = e;

        await viewModel.ConnectCommand.ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.Session.ServerName.Should().Be("Test Server");
        captured.Profile.Host.Should().Be("192.168.1.20");
        captured.Profile.DisplayName.Should().Be("Tom");
        viewModel.StatusMessage.Should().Be("Connected to Test Server.");
    }

    [Fact]
    public async Task Connect_PassesTheStableClientIdentityFromDisk()
    {
        var viewModel = WithOneValidProfile();

        await viewModel.ConnectCommand.ExecuteAsync(null);

        _connection.LastClientId.Should().Be(viewModel.ClientId);
        _connection.LastClientId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Connect_SavesEditsBeforeDialling()
    {
        // Losing your typed address because the connection failed would be maddening.
        var viewModel = WithOneValidProfile();
        _connection.NextAuthResult = AuthResult.Failed("nope");

        await viewModel.ConnectCommand.ExecuteAsync(null);

        var saved = _store.Load().Profiles.Should().ContainSingle().Subject;
        saved.Host.Should().Be("192.168.1.20");
        saved.DisplayName.Should().Be("Tom");
    }

    [Fact]
    public void Connect_WhileAlreadyConnecting_CannotExecute()
    {
        var viewModel = WithOneValidProfile();

        viewModel.ConnectCommand.CanExecute(null).Should().BeTrue();
        viewModel.IsConnecting = true;
        viewModel.ConnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Save_TrimsWhitespaceFromUserInput()
    {
        var viewModel = WithOneValidProfile();
        viewModel.SelectedProfile!.Host = "  10.0.0.5  ";
        viewModel.SelectedProfile.DisplayName = "  Tom  ";

        viewModel.SaveCommand.Execute(null);

        var saved = _store.Load().Profiles.Should().ContainSingle().Subject;
        saved.Host.Should().Be("10.0.0.5");
        saved.DisplayName.Should().Be("Tom");
    }

    [Fact]
    public void Reload_DiscardsUnsavedEdits()
    {
        var viewModel = WithOneValidProfile();
        viewModel.SaveCommand.Execute(null);
        viewModel.SelectedProfile!.Host = "999.999.999.999";

        viewModel.Reload();

        viewModel.Profiles[0].Host.Should().Be("192.168.1.20");
    }
}
