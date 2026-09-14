using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whisper.Server.Data;
using Whisper.Server.Hubs;
using Whisper.Shared;

namespace Whisper.Server;

/// <summary>
/// Seeds the database and prints the operator banner. This implements
/// <see cref="IHostedLifecycleService"/> rather than plain <see cref="IHostedService"/> so
/// the work completes before Kestrel starts accepting hub connections - clients must never
/// reach an unseeded server.
/// </summary>
public sealed class ServerStartupService(
    IServiceProvider services,
    ServerSettingsCache cache,
    IHubContext<WhisperHub, IWhisperClient> hub,
    IOptions<WhisperServerOptions> options,
    ILogger<ServerStartupService> logger) : IHostedLifecycleService
{
    private static readonly TimeSpan ShutdownNoticeTimeout = TimeSpan.FromSeconds(2);

    private readonly WhisperServerOptions _options = options.Value;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);

        await using var scope = services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        var result = await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        PrintBanner(result);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs before the transports close, which is the only window in which connected
    /// clients can still be told why they are about to be dropped. Without this they see
    /// an unexplained reconnect loop against a server that is gone.
    /// </summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Shutting down; telling connected clients.");

        try
        {
            await hub.Clients.All
                .ServerNotice("The server is shutting down.")
                .WaitAsync(ShutdownNoticeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Ctrl+C twice, or a wedged client. Shutting down matters more than the notice.
            logger.LogDebug("Gave up sending the shutdown notice.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Whisper server stopped.");
        return Task.CompletedTask;
    }

    private void PrintBanner(DatabaseInitializationResult initialization)
    {
        var scheme = _options.UseTls ? "https" : "http";

        logger.LogInformation("Whisper server \"{ServerName}\" is ready.", cache.Current.ServerName);
        logger.LogInformation(
            "Hub (chat, presence):  {Scheme}://0.0.0.0:{Port}{Path}",
            scheme,
            _options.HubPort,
            HubMethods.Path);
        logger.LogInformation("Voice relay:           udp://0.0.0.0:{Port}", _options.VoicePort);
        logger.LogInformation("Data directory:        {Path}", Path.GetFullPath(_options.DataDirectory));

        foreach (var address in LocalAddresses())
        {
            logger.LogInformation("Reachable on this LAN at {Address}:{Port}", address, _options.HubPort);
        }

        logger.LogInformation(
            "For connections from outside this network, forward TCP {HubPort} and UDP {VoicePort} to this machine.",
            _options.HubPort,
            _options.VoicePort);

        if (initialization.GeneratedPassword is { } password)
        {
            logger.LogWarning("No password was configured, so one was generated: {Password}", password);
            logger.LogWarning("Change it later with: --password <value> --reset-password true");
        }
    }

    private static IEnumerable<string> LocalAddresses() => NetworkInterface
        .GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == OperationalStatus.Up
            && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(address.Address))
        .Select(address => address.Address.ToString())
        .Distinct();
}
