using Microsoft.EntityFrameworkCore;

namespace Whisper.Server.Data;

public sealed class WhisperDbContext(DbContextOptions<WhisperDbContext> options) : DbContext(options)
{
    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    public DbSet<ServerSettingsEntity> ServerSettings => Set<ServerSettingsEntity>();

    public DbSet<KnownMemberEntity> KnownMembers => Set<KnownMemberEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Kind).HasConversion<int>();
            entity.HasIndex(e => e.Position);
        });

        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SenderName).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Content).HasMaxLength(4000).IsRequired();
            entity.HasIndex(e => new { e.ChannelId, e.SentUtcTicks });
        });

        modelBuilder.Entity<ServerSettingsEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ServerName).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<KnownMemberEntity>(entity =>
        {
            entity.HasKey(e => e.ClientId);
            entity.Property(e => e.LastDisplayName).HasMaxLength(64).IsRequired();
        });
    }
}
