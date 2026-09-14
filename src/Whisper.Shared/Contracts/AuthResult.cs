namespace Whisper.Shared.Contracts;

public sealed record AuthResult
{
    public bool Success { get; init; }

    public string? FailureReason { get; init; }

    public string ServerName { get; init; } = string.Empty;

    /// <summary>Single-use credential the client presents on the UDP handshake.</summary>
    public Guid VoiceToken { get; init; }

    public uint Ssrc { get; init; }

    public int VoicePort { get; init; }

    public IReadOnlyList<ChannelInfo> Channels { get; init; } = [];

    public IReadOnlyList<MemberInfo> Members { get; init; } = [];

    public static AuthResult Failed(string reason) => new() { Success = false, FailureReason = reason };

    public static AuthResult Ok(
        string serverName,
        Guid voiceToken,
        uint ssrc,
        int voicePort,
        IReadOnlyList<ChannelInfo> channels,
        IReadOnlyList<MemberInfo> members) => new()
        {
            Success = true,
            ServerName = serverName,
            VoiceToken = voiceToken,
            Ssrc = ssrc,
            VoicePort = voicePort,
            Channels = channels,
            Members = members,
        };
}
