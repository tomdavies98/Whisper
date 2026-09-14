using Microsoft.EntityFrameworkCore;
using Whisper.Shared.Contracts;

namespace Whisper.Server.Data;

public interface IChannelRepository
{
    Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default);

    Task<ChannelInfo?> FindAsync(int channelId, CancellationToken cancellationToken = default);
}

public sealed class ChannelRepository(WhisperDbContext db) : IChannelRepository
{
    public async Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.Channels
            .AsNoTracking()
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => r.ToInfo()).ToList();
    }

    public async Task<ChannelInfo?> FindAsync(int channelId, CancellationToken cancellationToken = default)
    {
        var row = await db.Channels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToInfo();
    }
}
