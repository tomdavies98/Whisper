using System.Net;
using Whisper.Client.Models;

namespace Whisper.Client.Audio;

/// <summary>
/// The client half of the media plane: microphone in, mixed speakers out, UDP in between.
/// </summary>
public interface IVoiceSession : IAsyncDisposable
{
    bool IsActive { get; }

    bool IsMuted { get; set; }

    bool IsDeafened { get; set; }

    /// <summary>True while audio is actually going out, i.e. the gate is open and not muted.</summary>
    bool IsTransmitting { get; }

    /// <summary>Latest microphone level as 0..1, for the input meter.</summary>
    float InputLevel { get; }

    AudioSettings Settings { get; }

    event EventHandler<float>? InputLevelChanged;

    event EventHandler<Exception>? Failed;

    Task<bool> StartAsync(
        IPEndPoint relayEndPoint,
        Guid voiceToken,
        uint ssrc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tears down the UDP session. Device handles stay open unless
    /// <paramref name="releaseDevices"/> is set, so leave-voice can rejoin immediately.
    /// </summary>
    Task StopAsync(bool releaseDevices = false);

    void ApplySettings(AudioSettings settings);

    void SetPushToTalkPressed(bool isPressed);
}
