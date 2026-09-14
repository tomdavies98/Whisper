using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Client.Audio;
using Whisper.Client.Infrastructure;
using Whisper.Client.Services;
using Whisper.Client.ViewModels;
using Xunit;

namespace Whisper.Tests.Client;

/// <summary>
/// The composition root is the one thing a WPF app cannot fail gracefully on: a missing
/// registration shows up as a blank window at launch. These run on every build instead.
/// The container holds an async-only disposable, so it must be disposed with
/// <c>await using</c> - which is also why <c>App.OnExit</c> awaits it.
/// </summary>
public class ClientServicesTests
{
    [Fact]
    public async Task Build_ResolvesTheShellAndEverythingItDependsOn()
    {
        using var directory = new TemporaryDirectory();
        await using var services = ClientServices.Build(directory.Path);

        var shell = services.GetRequiredService<ShellViewModel>();

        shell.Should().NotBeNull();
        shell.CurrentView.Should().BeSameAs(shell.Browser);
    }

    [Fact]
    public async Task Build_RegistersTheProfileStorePointingAtTheGivenDirectory()
    {
        using var directory = new TemporaryDirectory();
        await using var services = ClientServices.Build(directory.Path);

        var store = services.GetRequiredService<IProfileStore>();

        store.FilePath.Should().StartWith(directory.Path);
    }

    [Fact]
    public async Task Build_SharesOneConnectionBetweenTheBrowserAndTheSession()
    {
        // Two connections would mean the session view model listens to a socket that the
        // browser never dialled.
        using var directory = new TemporaryDirectory();
        await using var services = ClientServices.Build(directory.Path);

        var first = services.GetRequiredService<IWhisperConnection>();
        var second = services.GetRequiredService<IWhisperConnection>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task Build_SharesOneVoiceSessionBetweenTheSessionAndSettingsViews()
    {
        // The settings page adjusts the live call, so it has to be the same session the
        // main view started.
        using var directory = new TemporaryDirectory();
        await using var services = ClientServices.Build(directory.Path);

        var first = services.GetRequiredService<IVoiceSession>();
        var second = services.GetRequiredService<IVoiceSession>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task Build_GivesEachPeerItsOwnCodecInstance()
    {
        // Opus decoders are stateful, so sharing one across speakers produces garbage.
        using var directory = new TemporaryDirectory();
        await using var services = ClientServices.Build(directory.Path);

        var factory = services.GetRequiredService<Func<IAudioCodec>>();

        using var first = factory();
        using var second = factory();

        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public async Task Build_ResolvesTheSettingsViewModel()
    {
        using var directory = new TemporaryDirectory();
        await using var services = ClientServices.Build(directory.Path);

        services.GetRequiredService<SettingsViewModel>().Should().NotBeNull();
    }

    [Fact]
    public async Task Build_ValidatesEveryRegistrationEagerly()
    {
        using var directory = new TemporaryDirectory();

        var build = async () => await ClientServices.Build(directory.Path).DisposeAsync();

        await build.Should().NotThrowAsync();
    }
}
