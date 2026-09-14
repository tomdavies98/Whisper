using System.Text.Json.Serialization;
using Whisper.Shared;

namespace Whisper.Client.Models;

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New server";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = ProtocolLimits.DefaultHubPort;

    public string DisplayName { get; set; } = string.Empty;

    public bool UseTls { get; set; }

    /// <summary>
    /// Self-hosted servers almost always have a self-signed certificate, which the OS
    /// trust store will reject. Opting in per profile keeps that an explicit, per-server
    /// decision rather than a blanket weakening of TLS.
    /// </summary>
    public bool AllowSelfSignedCertificate { get; set; }

    public bool RememberPassword { get; set; }

    /// <summary>
    /// Only ever held in memory. The serialised form is <see cref="ProtectedPassword"/>,
    /// so a stolen profiles.json does not hand over server passwords in plain text.
    /// </summary>
    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public string? ProtectedPassword { get; set; }

    public string HubUrl =>
        $"{(UseTls ? "https" : "http")}://{Host}:{Port}{HubMethods.Path}";

    public ServerProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        DisplayName = DisplayName,
        UseTls = UseTls,
        AllowSelfSignedCertificate = AllowSelfSignedCertificate,
        RememberPassword = RememberPassword,
        Password = Password,
        ProtectedPassword = ProtectedPassword,
    };
}

public sealed class AudioSettings
{
    public string? InputDeviceId { get; set; }

    public string? OutputDeviceId { get; set; }

    /// <summary>When false, the voice-activity gate decides when to transmit.</summary>
    public bool UsePushToTalk { get; set; }

    /// <summary>Virtual key code for the push-to-talk key. Defaults to left control.</summary>
    public int PushToTalkKey { get; set; } = 0xA2;

    public float VoiceActivityThresholdDb { get; set; } = -45f;

    public int JitterBufferFrames { get; set; } = 3;

    public float InputGain { get; set; } = 1f;
}

/// <summary>The on-disk shape of profiles.json.</summary>
public sealed class ProfileDocument
{
    /// <summary>
    /// Stable identity for this installation. The server ties chat history authorship to
    /// it, so it must survive restarts and display-name changes.
    /// </summary>
    public Guid ClientId { get; set; } = Guid.NewGuid();

    public List<ServerProfile> Profiles { get; set; } = [];

    public AudioSettings Audio { get; set; } = new();
}
