using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Whisper.Shared;
using Whisper.Shared.Contracts;
using Xunit;

namespace Whisper.IntegrationTests;

/// <summary>
/// Tight per-connection budgets, so an abuse case is reached in a handful of calls. The
/// authentication budget stays loose here because it is keyed by source address and would
/// otherwise be shared by every test in the class.
/// </summary>
public sealed class ThrottledServerFixture : WhisperServerFixture
{
    public const int ChatBurst = 3;
    public const int ActionBurst = 5;

    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["Whisper:Password"] = Password,
        ["Whisper:ServerName"] = "Throttled Server",
        ["Whisper:VoicePort"] = "51002",
        ["Whisper:MaxMembers"] = "4",
        ["Whisper:ChatRateLimitBurst"] = ChatBurst.ToString(),
        ["Whisper:ChatRateLimitWindowSeconds"] = "60",
        ["Whisper:AuthRateLimitBurst"] = "10000",
        ["Whisper:ActionRateLimitBurst"] = ActionBurst.ToString(),
        ["Whisper:ActionRateLimitWindowSeconds"] = "60",
    };
}

/// <summary>
/// Its own server, because exhausting the address-keyed authentication budget locks out
/// every other test that would share it.
/// </summary>
public sealed class AuthThrottledServerFixture : WhisperServerFixture
{
    public const int AuthBurst = 4;

    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["Whisper:Password"] = Password,
        ["Whisper:ServerName"] = "Guarded Server",
        ["Whisper:VoicePort"] = "51003",
        ["Whisper:AuthRateLimitBurst"] = AuthBurst.ToString(),
        ["Whisper:AuthRateLimitWindowSeconds"] = "60",
    };
}

[Trait("Category", "Integration")]
public class AuthRateLimitTests(AuthThrottledServerFixture fixture) : IClassFixture<AuthThrottledServerFixture>
{
    [Fact]
    public async Task Authenticate_AfterRepeatedWrongPasswords_IsThrottledEvenOnANewConnection()
    {
        // Every attempt gets a fresh connection, which is exactly why this budget is keyed
        // by source address rather than connection id.
        for (var attempt = 0; attempt < AuthThrottledServerFixture.AuthBurst; attempt++)
        {
            await using var guess = fixture.CreateHubConnection();
            await guess.StartAsync();
            var result = await fixture.TryAuthenticateAsync(guess, $"guess-{attempt}");
            result.Success.Should().BeFalse();
        }

        await using var next = fixture.CreateHubConnection();
        await next.StartAsync();

        var throttled = await fixture.TryAuthenticateAsync(next, WhisperServerFixture.Password);

        throttled.Success.Should().BeFalse("even the correct password is refused once the budget is gone");
        throttled.FailureReason.Should().Contain("Too many attempts");
    }
}

/// <summary>One test per abuse case, each proving a specific guard does something.</summary>
[Trait("Category", "Integration")]
public class HubRateLimitTests(ThrottledServerFixture fixture) : IClassFixture<ThrottledServerFixture>
{
    [Fact]
    public async Task SendMessage_BeyondTheChatBudget_IsRejected()
    {
        var (connection, _) = await fixture.ConnectAsync("Spammer");

        await using (connection)
        {
            var channel = await FirstTextChannelAsync(connection);

            for (var i = 0; i < ThrottledServerFixture.ChatBurst; i++)
            {
                await connection.InvokeAsync<ChatMessage>(HubMethods.SendMessage, channel.Id, $"burst {i}");
            }

            var extra = async () =>
                await connection.InvokeAsync<ChatMessage>(HubMethods.SendMessage, channel.Id, "one too many");

            (await extra.Should().ThrowAsync<HubException>()).WithMessage("*too quickly*");
        }
    }

    [Fact]
    public async Task SendMessage_OverTheLengthCap_IsRejectedWithoutSpendingTheChatBudget()
    {
        var (connection, _) = await fixture.ConnectAsync("Verbose");

        await using (connection)
        {
            var channel = await FirstTextChannelAsync(connection);

            var overlong = async () => await connection.InvokeAsync<ChatMessage>(
                HubMethods.SendMessage,
                channel.Id,
                new string('x', ProtocolLimits.MaxMessageLength + 1));

            (await overlong.Should().ThrowAsync<HubException>())
                .WithMessage($"*{ProtocolLimits.MaxMessageLength} characters*");

            // The cap is checked before the budget, so a rejected message costs nothing.
            await connection.InvokeAsync<ChatMessage>(HubMethods.SendMessage, channel.Id, "still allowed");
        }
    }

    [Fact]
    public async Task SendMessage_AtExactlyTheLengthCap_IsAccepted()
    {
        var (connection, _) = await fixture.ConnectAsync("Precise");

        await using (connection)
        {
            var channel = await FirstTextChannelAsync(connection);

            var sent = await connection.InvokeAsync<ChatMessage>(
                HubMethods.SendMessage,
                channel.Id,
                new string('x', ProtocolLimits.MaxMessageLength));

            sent.Content.Should().HaveLength(ProtocolLimits.MaxMessageLength);
        }
    }

    [Fact]
    public async Task HubActions_BeyondTheActionBudget_AreRejected()
    {
        // Mute is cheap individually but broadcasts to everyone, so a script hammering it
        // costs the whole server.
        var (connection, _) = await fixture.ConnectAsync("Fidget");

        await using (connection)
        {
            for (var i = 0; i < ThrottledServerFixture.ActionBurst; i++)
            {
                await connection.InvokeAsync(HubMethods.SetMuted, i % 2 == 0);
            }

            var extra = async () => await connection.InvokeAsync(HubMethods.SetMuted, true);

            (await extra.Should().ThrowAsync<HubException>()).WithMessage("*Slow down*");
        }
    }

    private static async Task<ChannelInfo> FirstTextChannelAsync(HubConnection connection) =>
        (await connection.InvokeAsync<IReadOnlyList<ChannelInfo>>(HubMethods.GetChannels))
        .First(c => c.Kind == ChannelKind.Text);
}

/// <summary>
/// Abuse cases that need normal budgets, so they cannot share the throttled fixture.
/// </summary>
[Trait("Category", "Integration")]
public class HubAbuseTests(WhisperServerFixture fixture) : IClassFixture<WhisperServerFixture>
{
    [Fact]
    public async Task Authenticate_Twice_OnOneConnection_IsRejected()
    {
        // A second identity on one connection would leave an orphaned session behind.
        var (connection, _) = await fixture.ConnectAsync("Doubler");

        await using (connection)
        {
            var second = await connection.InvokeAsync<AuthResult>(
                HubMethods.Authenticate,
                WhisperServerFixture.Password,
                Guid.NewGuid(),
                "Doubler Again");

            second.Success.Should().BeFalse();
            second.FailureReason.Should().Contain("already authenticated");
        }
    }

    [Fact]
    public async Task Authenticate_WithAnEmptyClientId_IsRejected()
    {
        await using var connection = fixture.CreateHubConnection();
        await connection.StartAsync();

        var result = await connection.InvokeAsync<AuthResult>(
            HubMethods.Authenticate,
            WhisperServerFixture.Password,
            Guid.Empty,
            "Anonymous");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("client identity");
    }

    [Theory]
    [InlineData(HubMethods.GetChannels)]
    [InlineData(HubMethods.LeaveVoice)]
    public async Task AuthenticatedMethods_BeforeAuthenticating_AreRejected(string method)
    {
        await using var connection = fixture.CreateHubConnection();
        await connection.StartAsync();

        var call = async () => await connection.InvokeAsync(method);

        (await call.Should().ThrowAsync<HubException>()).WithMessage("*Not authenticated*");
    }

    [Fact]
    public async Task SendMessage_WithWhitespaceOnly_IsRejected()
    {
        var (connection, _) = await fixture.ConnectAsync("Blank");

        await using (connection)
        {
            var channel = (await connection.InvokeAsync<IReadOnlyList<ChannelInfo>>(HubMethods.GetChannels))
                .First(c => c.Kind == ChannelKind.Text);

            var send = async () =>
                await connection.InvokeAsync<ChatMessage>(HubMethods.SendMessage, channel.Id, "   \t  ");

            (await send.Should().ThrowAsync<HubException>()).WithMessage("*empty*");
        }
    }

    [Fact]
    public async Task SendMessage_ToAnUnknownChannel_IsRejected()
    {
        var (connection, _) = await fixture.ConnectAsync("Lost");

        await using (connection)
        {
            var send = async () =>
                await connection.InvokeAsync<ChatMessage>(HubMethods.SendMessage, 9999, "anyone there?");

            (await send.Should().ThrowAsync<HubException>()).WithMessage("*Unknown text channel*");
        }
    }

    [Fact]
    public async Task GetHistory_WithAnAbsurdPageSize_IsClampedRatherThanRefused()
    {
        var (connection, _) = await fixture.ConnectAsync("Greedy");

        await using (connection)
        {
            var channel = (await connection.InvokeAsync<IReadOnlyList<ChannelInfo>>(HubMethods.GetChannels))
                .First(c => c.Kind == ChannelKind.Text);

            var page = await connection.InvokeAsync<HistoryPage>(
                HubMethods.GetHistory, channel.Id, null, int.MaxValue);

            page.Messages.Count.Should().BeLessThanOrEqualTo(ProtocolLimits.MaxHistoryPageSize);
        }
    }
}
