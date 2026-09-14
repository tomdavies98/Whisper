using Whisper.Shared;

namespace Whisper.Server;

public sealed class WhisperServerOptions
{
    public const string SectionName = "Whisper";

    public string ServerName { get; set; } = "Whisper Server";

    public int HubPort { get; set; } = ProtocolLimits.DefaultHubPort;

    public int VoicePort { get; set; } = ProtocolLimits.DefaultVoicePort;

    /// <summary>Directory holding whisper.db and logs. Relative paths resolve against the working directory.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Plain-text password applied on first run only. After seeding, the database holds
    /// the PBKDF2 hash and this value is ignored unless <see cref="ResetPassword"/> is set.
    /// </summary>
    public string? Password { get; set; }

    public bool ResetPassword { get; set; }

    public int MaxMembers { get; set; } = 64;

    /// <summary>Chat messages a single connection may send per <see cref="ChatRateLimitWindowSeconds"/>.</summary>
    public int ChatRateLimitBurst { get; set; } = 10;

    public int ChatRateLimitWindowSeconds { get; set; } = 5;

    /// <summary>
    /// Authentication attempts allowed per source address. Deliberately tight: a shared
    /// password is only as strong as the number of guesses an attacker gets.
    /// </summary>
    public int AuthRateLimitBurst { get; set; } = 5;

    public int AuthRateLimitWindowSeconds { get; set; } = 30;

    /// <summary>Catch-all budget for the remaining hub methods, per connection.</summary>
    public int ActionRateLimitBurst { get; set; } = 30;

    public int ActionRateLimitWindowSeconds { get; set; } = 10;

    /// <summary>
    /// Audio datagrams accepted per session per second. Normal traffic is 50 (one 20 ms
    /// frame each); the headroom absorbs bursts after a network stall.
    /// </summary>
    public int VoicePacketsPerSecond { get; set; } = 100;

    /// <summary>Optional PFX certificate path. When set, the hub is served over HTTPS/WSS.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, "whisper.db");

    public bool UseTls => !string.IsNullOrWhiteSpace(CertificatePath);
}
