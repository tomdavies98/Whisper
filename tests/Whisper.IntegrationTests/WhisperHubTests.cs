using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Whisper.Shared;
using Whisper.Shared.Contracts;
using Xunit;

namespace Whisper.IntegrationTests;

[Trait("Category", "Integration")]
public class WhisperHubTests : IClassFixture<WhisperServerFixture>
{
    private readonly WhisperServerFixture _fixture;

    public WhisperHubTests(WhisperServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authenticate_WithCorrectPassword_ReturnsChannelsAndVoiceCredentials()
    {
        await using var connection = _fixture.CreateHubConnection();
        await connection.StartAsync();

        var result = await connection.InvokeAsync<AuthResult>(
            HubMethods.Authenticate,
            WhisperServerFixture.Password,
            Guid.NewGuid(),
            "Tom");

        result.Success.Should().BeTrue();
        result.ServerName.Should().Be("Test Server");
        result.VoiceToken.Should().NotBe(Guid.Empty);
        result.Ssrc.Should().NotBe(0u);
        result.VoicePort.Should().Be(51001);
        result.Channels.Should().Contain(c => c.Name == "general" && c.Kind == ChannelKind.Text);
        result.Channels.Should().Contain(c => c.Name == "Lobby" && c.Kind == ChannelKind.Voice);
    }

    [Fact]
    public async Task Authenticate_WithWrongPassword_IsRejectedWithoutVoiceCredentials()
    {
        await using var connection = _fixture.CreateHubConnection();
        await connection.StartAsync();

        var result = await connection.InvokeAsync<AuthResult>(
            HubMethods.Authenticate,
            "not-the-password",
            Guid.NewGuid(),
            "Intruder");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("Incorrect server password.");
        result.VoiceToken.Should().Be(Guid.Empty);
        result.Ssrc.Should().Be(0u);
    }

    [Fact]
    public async Task Authenticate_WithBlankDisplayName_IsRejected()
    {
        await using var connection = _fixture.CreateHubConnection();
        await connection.StartAsync();

        var result = await connection.InvokeAsync<AuthResult>(
            HubMethods.Authenticate,
            WhisperServerFixture.Password,
            Guid.NewGuid(),
            "   ");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("Display name");
    }

    [Fact]
    public async Task Authenticate_WithOverlongDisplayName_IsRejected()
    {
        await using var connection = _fixture.CreateHubConnection();
        await connection.StartAsync();

        var result = await connection.InvokeAsync<AuthResult>(
            HubMethods.Authenticate,
            WhisperServerFixture.Password,
            Guid.NewGuid(),
            new string('x', ProtocolLimits.MaxDisplayNameLength + 1));

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("Display name");
    }

    [Fact]
    public async Task SendMessage_BeforeAuthenticating_IsRejected()
    {
        await using var connection = _fixture.CreateHubConnection();
        await connection.StartAsync();

        var send = async () => await connection.InvokeAsync<ChatMessage>(HubMethods.SendMessage, 1, "hello");

        (await send.Should().ThrowAsync<HubException>()).WithMessage("*Not authenticated*");
    }

    [Fact]
    public async Task SendMessage_ReachesASecondConnectedClient()
    {
        var (sender, _) = await _fixture.ConnectAsync("Sender");
        var (receiver, receiverEvents) = await _fixture.ConnectAsync("Receiver");

        await using (sender)
        await using (receiver)
        {
            var textChannel = await FirstTextChannelAsync(sender);

            var sent = await sender.InvokeAsync<ChatMessage>(HubMethods.SendMessage, textChannel.Id, "hello from the test");
            var received = await receiverEvents.NextMessageAsync();

            received.Id.Should().Be(sent.Id);
            received.Content.Should().Be("hello from the test");
            received.SenderName.Should().Be("Sender");
            received.ChannelId.Should().Be(textChannel.Id);
        }
    }

    [Fact]
    public async Task Authenticate_AnnouncesTheNewMemberToExistingClients()
    {
        var (existing, existingEvents) = await _fixture.ConnectAsync("Existing");

        await using (existing)
        {
            var (arriving, _) = await _fixture.ConnectAsync("Arriving");
            await using (arriving)
            {
                var joined = await existingEvents.NextMemberJoinedAsync();
                joined.DisplayName.Should().Be("Arriving");
                joined.VoiceChannelId.Should().BeNull();
            }

            var leftId = await existingEvents.NextMemberLeftAsync();
            leftId.Should().NotBe(Guid.Empty);
        }
    }

    [Fact]
    public async Task JoinVoice_BroadcastsVoiceChannelMembership()
    {
        var (first, _) = await _fixture.ConnectAsync("First");
        var (second, secondEvents) = await _fixture.ConnectAsync("Second");

        await using (first)
        await using (second)
        {
            var voiceChannel = (await Channels(first)).First(c => c.Kind == ChannelKind.Voice);

            await first.InvokeAsync(HubMethods.JoinVoice, voiceChannel.Id);

            var updated = await secondEvents.NextMemberUpdatedAsync();
            updated.DisplayName.Should().Be("First");
            updated.VoiceChannelId.Should().Be(voiceChannel.Id);
        }
    }

    [Fact]
    public async Task JoinVoice_OnATextChannel_IsRejected()
    {
        var (connection, _) = await _fixture.ConnectAsync("Confused");

        await using (connection)
        {
            var textChannel = await FirstTextChannelAsync(connection);

            var join = async () => await connection.InvokeAsync(HubMethods.JoinVoice, textChannel.Id);

            (await join.Should().ThrowAsync<HubException>()).WithMessage("*Unknown voice channel*");
        }
    }

    [Fact]
    public async Task SetMuted_BroadcastsTheNewState()
    {
        var (first, _) = await _fixture.ConnectAsync("Muter");
        var (second, secondEvents) = await _fixture.ConnectAsync("Watcher");

        await using (first)
        await using (second)
        {
            await first.InvokeAsync(HubMethods.SetMuted, true);

            var updated = await secondEvents.NextMemberUpdatedAsync();
            updated.DisplayName.Should().Be("Muter");
            updated.IsMuted.Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetHistory_ReturnsMessagesPersistedByAPreviousConnection()
    {
        var clientId = Guid.NewGuid();
        int channelId;

        var (first, _) = await _fixture.ConnectAsync("Historian", clientId);
        await using (first)
        {
            channelId = (await FirstTextChannelAsync(first)).Id;
            await first.InvokeAsync<ChatMessage>(HubMethods.SendMessage, channelId, "survives a reconnect");
        }

        // Reconnecting is a brand new SignalR connection, so anything it can read came
        // from SQLite rather than in-memory state.
        var (second, _) = await _fixture.ConnectAsync("Historian", clientId);
        await using (second)
        {
            var page = await second.InvokeAsync<HistoryPage>(HubMethods.GetHistory, channelId, null, 50);

            page.Messages.Should().Contain(m => m.Content == "survives a reconnect");
        }
    }

    [Fact]
    public async Task GetHistory_OnAVoiceChannel_IsRejected()
    {
        var (connection, _) = await _fixture.ConnectAsync("Reader");

        await using (connection)
        {
            var voiceChannel = (await Channels(connection)).First(c => c.Kind == ChannelKind.Voice);

            var read = async () => await connection.InvokeAsync<HistoryPage>(
                HubMethods.GetHistory, voiceChannel.Id, null, 50);

            (await read.Should().ThrowAsync<HubException>()).WithMessage("*Unknown text channel*");
        }
    }

    private static async Task<IReadOnlyList<ChannelInfo>> Channels(HubConnection connection) =>
        await connection.InvokeAsync<IReadOnlyList<ChannelInfo>>(HubMethods.GetChannels);

    private static async Task<ChannelInfo> FirstTextChannelAsync(HubConnection connection) =>
        (await Channels(connection)).First(c => c.Kind == ChannelKind.Text);
}
