using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Whisper.Server;
using Whisper.Server.Hubs;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Server;

public class ServerStartupServiceTests
{
    private readonly IWhisperClient _allClients = Substitute.For<IWhisperClient>();
    private readonly IHubContext<WhisperHub, IWhisperClient> _hub =
        Substitute.For<IHubContext<WhisperHub, IWhisperClient>>();

    private readonly ServerStartupService _service;

    public ServerStartupServiceTests()
    {
        var clients = Substitute.For<IHubClients<IWhisperClient>>();
        clients.All.Returns(_allClients);
        _hub.Clients.Returns(clients);

        _service = new ServerStartupService(
            new ServiceCollection().BuildServiceProvider(),
            new ServerSettingsCache(),
            _hub,
            Options.Create(new WhisperServerOptions()),
            NullLogger<ServerStartupService>.Instance);
    }

    [Fact]
    public async Task Stopping_TellsConnectedClientsWhyTheyAreAboutToBeDropped()
    {
        // Without this the client just sees its connection drop and starts reconnecting
        // against a server that is gone.
        await _service.StoppingAsync(CancellationToken.None);

        await _allClients.Received(1).ServerNotice("The server is shutting down.");
    }

    [Fact]
    public async Task Stopping_WhenAClientIsWedged_StillCompletes()
    {
        _allClients.ServerNotice(Arg.Any<string>()).Returns(Task.Delay(Timeout.Infinite));

        var stopping = async () => await _service.StoppingAsync(CancellationToken.None);

        await stopping.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Stopping_WhenShutdownIsAlreadyCancelled_DoesNotThrow()
    {
        // Ctrl+C twice: the host cancels the shutdown token and the notice is abandoned.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var stopping = async () => await _service.StoppingAsync(cancelled.Token);

        await stopping.Should().NotThrowAsync();
    }
}
