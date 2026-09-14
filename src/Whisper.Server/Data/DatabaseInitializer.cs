using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whisper.Server.Security;
using Whisper.Shared.Contracts;

namespace Whisper.Server.Data;

public sealed record DatabaseInitializationResult(bool CreatedServer, string? GeneratedPassword);

public sealed class DatabaseInitializer(
    WhisperDbContext db,
    IPasswordHasher passwordHasher,
    ServerSettingsCache cache,
    IOptions<WhisperServerOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    private readonly WhisperServerOptions _options = options.Value;

    public async Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Prefer real migrations when any have been generated, and fall back to schema
        // creation so a fresh clone runs without the EF tooling installed.
        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        var settings = await db.ServerSettings
            .FirstOrDefaultAsync(s => s.Id == ServerSettingsEntity.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        string? generatedPassword = null;
        var created = false;

        if (settings is null)
        {
            generatedPassword = string.IsNullOrWhiteSpace(_options.Password) ? GeneratePassword() : null;
            var password = generatedPassword ?? _options.Password!;
            var hash = passwordHasher.Hash(password);

            settings = new ServerSettingsEntity
            {
                Id = ServerSettingsEntity.SingletonId,
                ServerName = Truncate(_options.ServerName),
                PasswordHash = hash.Hash,
                PasswordSalt = hash.Salt,
                MaxMembers = _options.MaxMembers,
            };

            db.ServerSettings.Add(settings);
            SeedChannels();
            created = true;
        }
        else
        {
            settings.ServerName = Truncate(_options.ServerName);
            settings.MaxMembers = _options.MaxMembers;

            if (_options.ResetPassword && !string.IsNullOrWhiteSpace(_options.Password))
            {
                var hash = passwordHasher.Hash(_options.Password);
                settings.PasswordHash = hash.Hash;
                settings.PasswordSalt = hash.Salt;
                logger.LogWarning("Server password was reset from configuration.");
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        cache.Current = new ServerSettingsSnapshot(
            settings.ServerName,
            settings.PasswordHash,
            settings.PasswordSalt,
            settings.MaxMembers);

        return new DatabaseInitializationResult(created, generatedPassword);
    }

    private void SeedChannels()
    {
        var nowTicks = timeProvider.GetUtcNow().UtcTicks;

        db.Channels.AddRange(
            new ChannelEntity { Name = "general", Kind = ChannelKind.Text, Position = 0 },
            new ChannelEntity { Name = "off-topic", Kind = ChannelKind.Text, Position = 1 },
            new ChannelEntity { Name = "Lobby", Kind = ChannelKind.Voice, Position = 0 },
            new ChannelEntity { Name = "Gaming", Kind = ChannelKind.Voice, Position = 1 });

        logger.LogInformation("Seeded default channels at {Timestamp:u}.", new DateTime(nowTicks, DateTimeKind.Utc));
    }

    private static string Truncate(string value) =>
        value.Length <= 64 ? value : value[..64];

    private static string GeneratePassword()
    {
        // Ambiguous characters removed so the operator can read it out of the console.
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(16, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }
}
