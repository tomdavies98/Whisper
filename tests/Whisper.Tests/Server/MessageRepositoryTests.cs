using FluentAssertions;
using Whisper.Server.Data;
using Whisper.Shared;
using Whisper.Shared.Contracts;
using Xunit;

namespace Whisper.Tests.Server;

public class MessageRepositoryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Author = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AddAsync_ReturnsPersistedMessageWithServerAssignedId()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var channelId = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var repository = new MessageRepository(fixture.Db);

        var message = await repository.AddAsync(channelId, Author, "Tom", "hello", Origin);

        message.Id.Should().BeGreaterThan(0);
        message.ChannelId.Should().Be(channelId);
        message.SenderClientId.Should().Be(Author);
        message.SenderName.Should().Be("Tom");
        message.Content.Should().Be("hello");
        message.SentUtc.Should().Be(Origin);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsNewestFirst()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var channelId = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var repository = new MessageRepository(fixture.Db);

        await repository.AddAsync(channelId, Author, "Tom", "first", Origin);
        await repository.AddAsync(channelId, Author, "Tom", "second", Origin.AddSeconds(1));
        await repository.AddAsync(channelId, Author, "Tom", "third", Origin.AddSeconds(2));

        var page = await repository.GetHistoryAsync(channelId, before: null, take: 10);

        page.Messages.Select(m => m.Content).Should().Equal("third", "second", "first");
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetHistoryAsync_WhenMoreRowsExist_ReportsHasMore()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var channelId = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var repository = new MessageRepository(fixture.Db);

        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(channelId, Author, "Tom", $"message {i}", Origin.AddSeconds(i));
        }

        var page = await repository.GetHistoryAsync(channelId, before: null, take: 2);

        page.Messages.Should().HaveCount(2);
        page.Messages.Select(m => m.Content).Should().Equal("message 4", "message 3");
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryAsync_WithBeforeCutoff_PagesBackwardsWithoutGapsOrRepeats()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var channelId = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var repository = new MessageRepository(fixture.Db);

        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(channelId, Author, "Tom", $"message {i}", Origin.AddSeconds(i));
        }

        var firstPage = await repository.GetHistoryAsync(channelId, before: null, take: 2);
        var oldestSoFar = firstPage.Messages[^1].SentUtc;
        var secondPage = await repository.GetHistoryAsync(channelId, before: oldestSoFar, take: 2);

        secondPage.Messages.Select(m => m.Content).Should().Equal("message 2", "message 1");
        secondPage.HasMore.Should().BeTrue();

        var thirdPage = await repository.GetHistoryAsync(channelId, before: secondPage.Messages[^1].SentUtc, take: 2);
        thirdPage.Messages.Select(m => m.Content).Should().Equal("message 0");
        thirdPage.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetHistoryAsync_IgnoresOtherChannels()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var general = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var offTopic = await fixture.AddChannelAsync("off-topic", ChannelKind.Text, position: 1);
        var repository = new MessageRepository(fixture.Db);

        await repository.AddAsync(general, Author, "Tom", "in general", Origin);
        await repository.AddAsync(offTopic, Author, "Tom", "in off-topic", Origin.AddSeconds(1));

        var page = await repository.GetHistoryAsync(general, before: null, take: 10);

        page.Messages.Should().HaveCount(1);
        page.Messages[0].Content.Should().Be("in general");
    }

    [Fact]
    public async Task GetHistoryAsync_AfterAuthorRenames_KeepsTheNameUsedAtSendTime()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var channelId = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var repository = new MessageRepository(fixture.Db);

        await repository.AddAsync(channelId, Author, "Tom", "sent under the old name", Origin);
        await repository.AddAsync(channelId, Author, "Tommy", "sent under the new name", Origin.AddSeconds(1));

        var page = await repository.GetHistoryAsync(channelId, before: null, take: 10);

        page.Messages.Single(m => m.Content == "sent under the old name").SenderName.Should().Be("Tom");
        page.Messages.Single(m => m.Content == "sent under the new name").SenderName.Should().Be("Tommy");
    }

    [Fact]
    public async Task GetHistoryAsync_TakeAboveMaximum_IsClamped()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var channelId = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var repository = new MessageRepository(fixture.Db);

        for (var i = 0; i < ProtocolLimits.MaxHistoryPageSize + 10; i++)
        {
            await repository.AddAsync(channelId, Author, "Tom", $"message {i}", Origin.AddSeconds(i));
        }

        var page = await repository.GetHistoryAsync(channelId, before: null, take: int.MaxValue);

        page.Messages.Should().HaveCount(ProtocolLimits.MaxHistoryPageSize);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryAsync_EmptyChannel_ReturnsEmptyPage()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var channelId = await fixture.AddChannelAsync("general", ChannelKind.Text);
        var repository = new MessageRepository(fixture.Db);

        var page = await repository.GetHistoryAsync(channelId, before: null, take: 10);

        page.Messages.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.ChannelId.Should().Be(channelId);
    }
}
