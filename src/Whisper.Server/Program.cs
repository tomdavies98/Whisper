using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Whisper.Server;
using Whisper.Server.Data;
using Whisper.Server.Hubs;
using Whisper.Server.RateLimiting;
using Whisper.Server.Security;
using Whisper.Server.Voice;
using Whisper.Shared;
using Whisper.Shared.Net;

var builder = WebApplication.CreateBuilder(args);

// The host only looks for appsettings.json in the working directory, so a published
// server launched from anywhere else would silently run on defaults. Added after the
// host's own sources and before the command line, which keeps the expected precedence:
// flags beat the file next to the executable, which beats the working directory.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: false);

builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["--port"] = "Whisper:HubPort",
    ["--voice-port"] = "Whisper:VoicePort",
    ["--name"] = "Whisper:ServerName",
    ["--password"] = "Whisper:Password",
    ["--reset-password"] = "Whisper:ResetPassword",
    ["--data"] = "Whisper:DataDirectory",
    ["--max-members"] = "Whisper:MaxMembers",
    ["--cert"] = "Whisper:CertificatePath",
    ["--cert-password"] = "Whisper:CertificatePassword",
});

// Read once for the pieces that must be configured before the container exists.
// Everything else resolves IOptions at runtime so tests can override settings.
var startupOptions = builder.Configuration
    .GetSection(WhisperServerOptions.SectionName)
    .Get<WhisperServerOptions>() ?? new WhisperServerOptions();

builder.Host.UseSerilog((context, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(startupOptions.DataDirectory, "whisper-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14));

builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(startupOptions.HubPort, listen =>
{
    if (startupOptions.UseTls)
    {
        listen.UseHttps(startupOptions.CertificatePath!, startupOptions.CertificatePassword);
    }
}));

builder.Services.Configure<WhisperServerOptions>(
    builder.Configuration.GetSection(WhisperServerOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerSettingsCache>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<HubRateLimits>();

builder.Services.AddDbContext<WhisperDbContext>((sp, db) =>
{
    var options = sp.GetRequiredService<IOptions<WhisperServerOptions>>().Value;
    Directory.CreateDirectory(options.DataDirectory);
    db.UseSqlite($"Data Source={options.DatabasePath}");
});
builder.Services.AddScoped<IChannelRepository, ChannelRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<DatabaseInitializer>();

builder.Services.AddSingleton<IUdpTransport, UdpSocketTransport>();
builder.Services.AddSingleton<VoiceRouter>();
builder.Services.AddSingleton<SpeakingTracker>();
builder.Services.AddSingleton<IVoiceNotifier, HubVoiceNotifier>();
builder.Services.AddSingleton<UdpVoiceRelay>();

builder.Services.AddHostedService<ServerStartupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpVoiceRelay>());

builder.Services.AddSignalR(hub =>
{
    hub.EnableDetailedErrors = builder.Environment.IsDevelopment();

    // A chat message is capped at 2000 characters, so anything near this size is either
    // a bug or an attempt to make the server allocate on demand.
    hub.MaximumReceiveMessageSize = 32 * 1024;
    hub.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    hub.KeepAliveInterval = TimeSpan.FromSeconds(10);
});

// Long enough to deliver the shutdown notice, short enough that Ctrl+C still feels
// immediate to whoever is running the server.
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(5));

var app = builder.Build();

app.MapHub<WhisperHub>(HubMethods.Path);
app.MapGet("/health", (ServerSettingsCache cache, SessionRegistry sessions) => Results.Json(new
{
    serverName = cache.Current.ServerName,
    members = sessions.Count,
}));

await app.RunAsync();

public partial class Program;
