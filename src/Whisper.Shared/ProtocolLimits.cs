namespace Whisper.Shared;

public static class ProtocolLimits
{
    public const int MaxMessageLength = 2000;
    public const int MaxDisplayNameLength = 32;
    public const int MaxServerNameLength = 64;
    public const int DefaultHistoryPageSize = 50;
    public const int MaxHistoryPageSize = 200;

    public const int DefaultHubPort = 5000;
    public const int DefaultVoicePort = 5001;

    public static readonly TimeSpan VoiceKeepaliveInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan VoiceSessionTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SpeakingDecay = TimeSpan.FromMilliseconds(200);
}
