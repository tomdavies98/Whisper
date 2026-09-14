using System.IO;
using System.Text.Json;
using Whisper.Client.Models;

namespace Whisper.Client.Services;

public interface IProfileStore
{
    string FilePath { get; }

    ProfileDocument Load();

    void Save(ProfileDocument document);
}

/// <summary>
/// Persists servers and audio settings as JSON under the user's roaming profile. Loading
/// is forgiving by design: a damaged file must not stop the app from starting, because
/// the alternative is a client that cannot be launched to fix itself.
/// </summary>
public sealed class ProfileStore : IProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ISecretProtector _protector;

    public ProfileStore(ISecretProtector protector, string? directory = null)
    {
        _protector = protector;
        Directory = directory ?? DefaultDirectory();
        FilePath = Path.Combine(Directory, "profiles.json");
    }

    public string Directory { get; }

    public string FilePath { get; }

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Whisper");

    public ProfileDocument Load()
    {
        if (!File.Exists(FilePath))
        {
            return new ProfileDocument();
        }

        ProfileDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath), SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ProfileDocument();
        }

        if (document is null)
        {
            return new ProfileDocument();
        }

        if (document.ClientId == Guid.Empty)
        {
            document.ClientId = Guid.NewGuid();
        }

        document.Audio ??= new AudioSettings();
        document.Profiles ??= [];

        foreach (var profile in document.Profiles)
        {
            if (profile.ProtectedPassword is { Length: > 0 } secret
                && _protector.TryUnprotect(secret, out var password))
            {
                profile.Password = password;
            }
            else
            {
                profile.Password = string.Empty;
                profile.ProtectedPassword = null;
            }
        }

        return document;
    }

    public void Save(ProfileDocument document)
    {
        System.IO.Directory.CreateDirectory(Directory);

        foreach (var profile in document.Profiles)
        {
            profile.ProtectedPassword = profile is { RememberPassword: true, Password.Length: > 0 }
                ? _protector.Protect(profile.Password)
                : null;
        }

        // Write to a temporary file first so a crash mid-write cannot leave the user with
        // a truncated profile list.
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, SerializerOptions));
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
