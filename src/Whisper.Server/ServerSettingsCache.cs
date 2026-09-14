namespace Whisper.Server;

public sealed record ServerSettingsSnapshot(
    string ServerName,
    byte[] PasswordHash,
    byte[] PasswordSalt,
    int MaxMembers);

/// <summary>
/// Holds the seeded server identity in memory so the auth path does not hit SQLite on
/// every connection attempt.
/// </summary>
public sealed class ServerSettingsCache
{
    private ServerSettingsSnapshot _current = new("Whisper Server", [], [], 0);

    public ServerSettingsSnapshot Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
