using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whisper.Client.Audio;
using Whisper.Client.Services;
using Whisper.Client.ViewModels;
using Whisper.Shared.Net;

namespace Whisper.Client.Infrastructure;

/// <summary>
/// The client's composition root. Building the container is a pure function of the
/// profile directory, which lets a test assert every service resolves without launching
/// the WPF application.
/// </summary>
public static class ClientServices
{
    public static ServiceProvider Build(string? profileDirectory = null)
    {
        var services = new ServiceCollection();

        services.AddLogging(logging => logging.AddDebug().SetMinimumLevel(LogLevel.Information));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IAudioThread, DedicatedAudioThread>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IProfileStore>(sp =>
            new ProfileStore(sp.GetRequiredService<ISecretProtector>(), profileDirectory));
        services.AddSingleton<IWhisperConnection, SignalRWhisperConnection>();

        services.AddSingleton<Func<IUdpTransport>>(_ => static () => new UdpSocketTransport());
        services.AddSingleton<Func<IAudioCodec>>(_ => static () => new OpusAudioCodec());
        services.AddSingleton<IAudioDeviceProvider, NAudioDeviceProvider>();
        services.AddSingleton<IAudioCapture, NAudioCapture>();
        services.AddSingleton<IAudioOutput, NAudioOutput>();
        services.AddSingleton<IVoiceSession, VoiceSession>();
        services.AddSingleton<PushToTalkMonitor>();

        services.AddSingleton<ServerBrowserViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
