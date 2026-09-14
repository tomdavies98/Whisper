using Microsoft.EntityFrameworkCore;
using Whisper.Shared;
using Whisper.Shared.Contracts;

namespace Whisper.Server.Data;

public interface IMessageRepository
{
    Task<ChatMessage> AddAsync(
        int channelId,
        Guid senderClientId,
        string senderName,
        string content,
        DateTimeOffset sentUtc,
        CancellationToken cancellationToken = default);

    Task<HistoryPage> GetHistoryAsync(
        int channelId,
        DateTimeOffset? before,
        int take,
        CancellationToken cancellationToken = default);
}

public sealed class MessageRepository(WhisperDbContext db) : IMessageRepository
{
    public async Task<ChatMessage> AddAsync(
        int channelId,
        Guid senderClientId,
        string senderName,
        string content,
        DateTimeOffset sentUtc,
        CancellationToken cancellationToken = default)
    {
        var entity = new MessageEntity
        {
            ChannelId = channelId,
            SenderClientId = senderClientId,
            SenderName = senderName,
            Content = content,
            SentUtcTicks = sentUtc.UtcTicks,
        };

        db.Messages.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity.ToContract();
    }

    /// <summary>
    /// Returns the newest messages first, so the client can prepend a page when the user
    /// scrolls up. One extra row is read to decide <see cref="HistoryPage.HasMore"/>
    /// without a second count query.
    /// </summary>
    public async Task<HistoryPage> GetHistoryAsync(
        int channelId,
        DateTimeOffset? before,
        int take,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, ProtocolLimits.MaxHistoryPageSize);

        var query = db.Messages.AsNoTracking().Where(m => m.ChannelId == channelId);

        if (before is { } cutoff)
        {
            var cutoffTicks = cutoff.UtcTicks;
            query = query.Where(m => m.SentUtcTicks < cutoffTicks);
        }

        var rows = await query
            .OrderByDescending(m => m.SentUtcTicks)
            .ThenByDescending(m => m.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new HistoryPage(channelId, rows.Select(r => r.ToContract()).ToList(), hasMore);
    }
}
