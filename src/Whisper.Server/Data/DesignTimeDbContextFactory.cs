using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Whisper.Server.Data;

/// <summary>
/// Used only by `dotnet ef`. Keeping it explicit means migration commands never have to
/// spin up the real host, bind ports, or touch the operator's database.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WhisperDbContext>
{
    public WhisperDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WhisperDbContext>()
            .UseSqlite("Data Source=whisper-design-time.db")
            .Options;

        return new WhisperDbContext(options);
    }
}
