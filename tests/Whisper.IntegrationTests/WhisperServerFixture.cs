using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whisper.Server.Data;
using Whisper.Shared;
using Whisper.Shared.Contracts;

namespace Whisper.IntegrationTests;

/// <summary>
/// Hosts the real server in-process with its SQLite database held in memory. The hub is
/// exercised through an actual <see cref="HubConnection"/> so serialisation and the
/// strongly-typed client callbacks are covered, not just the hub methods in isolation.
/// </summary>
public class WhisperServerFixture : WebApplicationFactory<Program>
{
    public const string Password = "integration-test-password";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public WhisperServerFixture()
    {
        // Program reads a few settings eagerly, before the factory can call UseSetting,
        // so the data directory is redirected through the environment instead.
        Environment.SetEnvironmentVariable(
            "Whisper__DataDirectory",
            Path.Combine(Path.GetTempPath(), "whisper-tests", Guid.NewGuid().ToString("N")));

        _connection.Open();
    }

    /// <summary>
    /// Server settings for this fixture. Overridden by fixtures that need tighter limits
    /// than the defaults, which are deliberately loose so a whole test class can share one
    /// server without tests starving each other's budgets.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["Whisper:Password"] = Password,
        ["Whisper:ServerName"] = "Test Server",
        ["Whisper:VoicePort"] = "51001",
        ["Whisper:MaxMembers"] = "4",
        ["Whisper:ChatRateLimitBurst"] = "5",
        ["Whisper:ChatRateLimitWindowSeconds"] = "5",
        ["Whisper:AuthRateLimitBurst"] = "10000",
        ["Whisper:ActionRateLimitBurst"] = "10000",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WhisperDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<WhisperDbContext>(db => db.UseSqlite(_connection));
        });
    }

    /// <summary>Creates a hub connection routed through the in-memory test server.</summary>
    public HubConnection CreateHubConnection() => new HubConnectionBuilder()
        .WithUrl(new Uri(Server.BaseAddress, HubMethods.Path.TrimStart('/')), options =>
        {
            options.HttpMessageHandlerFactory = _ => Server.CreateHandler();

            // TestServer has no real socket to upgrade, so long polling is the transport
            // that exercises the hub without standing up Kestrel on a port.
            options.Transports = HttpTransportType.LongPolling;
        })
        .Build();

    public async Task<(HubConnection Connection, TestClientEvents Events)> ConnectAsync(
        string displayName,
        Guid? clientId = null,
        string? password = null)
    {
        var connection = CreateHubConnection();
        var events = new TestClientEvents(connection);
        await connection.StartAsync();

        var result = await connection.InvokeAsync<AuthResult>(
            HubMethods.Authenticate,
            password ?? Password,
            clientId ?? Guid.NewGuid(),
            displayName);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Authentication failed: {result.FailureReason}");
        }

        events.AuthResult = result;
        return (connection, events);
    }

    public async Task<AuthResult> TryAuthenticateAsync(HubConnection connection, string password)
    {
        return await connection.InvokeAsync<AuthResult>(
            HubMethods.Authenticate,
            password,
            Guid.NewGuid(),
            "Attempt");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
