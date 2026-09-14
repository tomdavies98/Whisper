using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whisper.Server.Data;
using Whisper.Shared.Contracts;

namespace Whisper.Tests.Server;

/// <summary>
/// A real SQLite database held in memory. The connection stays open for the lifetime of
/// the fixture because SQLite discards an in-memory database as soon as the last
/// connection to it closes.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private TestDatabase(SqliteConnection connection, WhisperDbContext db)
    {
        _connection = connection;
        Db = db;
    }

    public WhisperDbContext Db { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<WhisperDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new WhisperDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestDatabase(connection, db);
    }

    public async Task<int> AddChannelAsync(string name, ChannelKind kind, int position = 0)
    {
        var entity = new ChannelEntity { Name = name, Kind = kind, Position = position };
        Db.Channels.Add(entity);
        await Db.SaveChangesAsync();
        return entity.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
