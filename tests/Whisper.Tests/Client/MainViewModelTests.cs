using System.Net;
using FluentAssertions;
using Whisper.Client.Infrastructure;
using Whisper.Client.Models;
using Whisper.Client.Services;
using Whisper.Client.ViewModels;
using Whisper.Shared;
using Whisper.Shared.Contracts;
using Xunit;

namespace Whisper.Tests.Client;

public class MainViewModelTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChannelInfo General = new(1, "general", ChannelKind.Text, 0);
    private static readonly ChannelInfo OffTopic = new(2, "off-topic", ChannelKind.Text, 1);
    private static readonly ChannelInfo Lobby = new(3, "Lobby", ChannelKind.Voice, 0);

    private static readonly ServerProfile Profile = new()
    {
        Name = "Test Server",
        Host = "127.0.0.1",
        Port = 5000,
        DisplayName = "Me",
    };

    private readonly FakeWhisperConnection _connection = new();
    private readonly FakeVoiceSession _voice = new();
    private readonly InMemoryProfileStore _profileStore = new();
    private readonly MainViewModel _viewModel;

    public MainViewModelTests() => _viewModel = new MainViewModel(
        _connection,
        new ImmediateUiDispatcher(),
        _voice,
        _profileStore,
        new PushToTalkMonitor());

    private static MemberInfo Member(string name, Guid? id = null, uint ssrc = 10) =>
        new(id ?? Guid.NewGuid(), name, ssrc, null, false, false);

    private static ChatMessage Message(long id, int channelId = 1, string content = "hello", int secondsOffset = 0) =>
        new(id, channelId, Guid.NewGuid(), "Someone", content, Origin.AddSeconds(secondsOffset));

    private async Task<AuthResult> AttachAsync(params MemberInfo[] members)
    {
        var session = AuthResult.Ok(
            "Test Server",
            Guid.NewGuid(),
            ssrc: 99,
            voicePort: 5001,
            channels: [General, OffTopic, Lobby],
            members: members);

        // The shell only attaches after the browser has authenticated.
        _connection.State = ClientConnectionState.Connected;

        await _viewModel.AttachAsync(Profile, session);
        return session;
    }

    [Fact]
    public async Task Attach_SplitsChannelsByKind()
    {
        await AttachAsync();

        _viewModel.ServerName.Should().Be("Test Server");
        _viewModel.TextChannels.Select(c => c.Name).Should().Equal("general", "off-topic");
        _viewModel.VoiceChannels.Select(c => c.Name).Should().Equal("Lobby");
    }

    [Fact]
    public async Task Attach_NestsMembersAlreadyInVoiceUnderThatChannel()
    {
        var ada = new MemberInfo(Guid.NewGuid(), "Ada", 10, Lobby.Id, false, false);
        var grace = Member("Grace");

        await AttachAsync(ada, grace);

        var lobby = _viewModel.VoiceChannels.Should().ContainSingle().Which;
        lobby.Occupants.Select(m => m.DisplayName).Should().Equal("Ada");
        lobby.Occupants[0].Should().BeSameAs(_viewModel.Members.Single(m => m.DisplayName == "Ada"));
    }

    [Fact]
    public async Task Attach_SortsOccupantsByDisplayName()
    {
        await AttachAsync(
            new MemberInfo(Guid.NewGuid(), "Zoe", 10, Lobby.Id, false, false),
            new MemberInfo(Guid.NewGuid(), "Ada", 11, Lobby.Id, false, false));

        _viewModel.VoiceChannels.Single().Occupants.Select(m => m.DisplayName).Should().Equal("Ada", "Zoe");
    }

    [Fact]
    public async Task Attach_SelectsTheFirstTextChannelAndLoadsExactlyOnePageOfHistory()
    {
        _connection.NextHistoryPage = new HistoryPage(1, [Message(1)], false);

        await AttachAsync();

        _viewModel.SelectedTextChannel.Should().Be(General);
        _connection.HistoryRequests.Should().ContainSingle()
            .Which.Should().Be((1, (DateTimeOffset?)null, ProtocolLimits.DefaultHistoryPageSize));
        _viewModel.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Attach_PopulatesTheMemberList()
    {
        await AttachAsync(Member("Ada"), Member("Grace"));

        _viewModel.Members.Select(m => m.DisplayName).Should().Equal("Ada", "Grace");
    }

    [Fact]
    public async Task Attach_OrdersHistoryOldestFirst()
    {
        // The server hands back newest first; the transcript has to read the other way.
        _connection.NextHistoryPage = new HistoryPage(
            1,
            [Message(3, content: "third", secondsOffset: 2), Message(2, content: "second", secondsOffset: 1), Message(1, content: "first")],
            false);

        await AttachAsync();

        _viewModel.Messages.Select(m => m.Content).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task MessageReceived_ForTheOpenChannel_IsAppended()
    {
        await AttachAsync();

        _connection.RaiseMessageReceived(Message(50, content: "live"));

        _viewModel.Messages.Select(m => m.Content).Should().Equal("live");
    }

    [Fact]
    public async Task MessageReceived_ForADifferentChannel_IsNotShown()
    {
        await AttachAsync();

        _connection.RaiseMessageReceived(Message(50, channelId: OffTopic.Id, content: "elsewhere"));

        _viewModel.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task MessageReceived_Twice_IsShownOnce()
    {
        // The sender receives its own broadcast, and a reconnect can replay one.
        await AttachAsync();
        var message = Message(50, content: "once");

        _connection.RaiseMessageReceived(message);
        _connection.RaiseMessageReceived(message);

        _viewModel.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task SelectingAnotherChannel_ClearsTheTranscriptAndLoadsThatChannel()
    {
        await AttachAsync();
        _connection.RaiseMessageReceived(Message(50));
        _connection.NextHistoryPage = new HistoryPage(2, [Message(60, channelId: 2, content: "other")], false);

        _viewModel.SelectedTextChannel = OffTopic;

        _viewModel.Messages.Select(m => m.Content).Should().Equal("other");
        _connection.HistoryRequests.Last().ChannelId.Should().Be(2);
    }

    [Fact]
    public async Task Send_TransmitsTheDraftAndClearsTheBox()
    {
        await AttachAsync();
        _viewModel.MessageDraft = "  hello there  ";

        await _viewModel.SendCommand.ExecuteAsync(null);

        _connection.SentMessages.Should().ContainSingle().Which.Should().Be((1, "hello there"));
        _viewModel.MessageDraft.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_WithAnEmptyDraft_DoesNothing()
    {
        await AttachAsync();
        _viewModel.MessageDraft = "   ";

        await _viewModel.SendCommand.ExecuteAsync(null);

        _connection.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_WhenTheServerRejects_RestoresTheDraftSoNothingIsLost()
    {
        await AttachAsync();
        _connection.SendMessageException = new InvalidOperationException("You are sending messages too quickly.");
        _viewModel.MessageDraft = "please keep this";

        await _viewModel.SendCommand.ExecuteAsync(null);

        _viewModel.MessageDraft.Should().Be("please keep this");
        _viewModel.StatusMessage.Should().Be("You are sending messages too quickly.");
    }

    [Fact]
    public async Task Send_OverTheLengthLimit_IsRefusedLocally()
    {
        await AttachAsync();
        _viewModel.MessageDraft = new string('x', ProtocolLimits.MaxMessageLength + 1);

        await _viewModel.SendCommand.ExecuteAsync(null);

        _connection.SentMessages.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Contain($"{ProtocolLimits.MaxMessageLength} characters");
    }

    [Fact]
    public async Task LoadOlder_PrependsThePreviousPageAndKeepsChronology()
    {
        _connection.NextHistoryPage = new HistoryPage(1, [Message(2, content: "newer", secondsOffset: 10)], true);
        await AttachAsync();

        _connection.NextHistoryPage = new HistoryPage(1, [Message(1, content: "older")], false);
        await _viewModel.LoadOlderCommand.ExecuteAsync(null);

        _viewModel.Messages.Select(m => m.Content).Should().Equal("older", "newer");
        _viewModel.HasMoreHistory.Should().BeFalse();
        _connection.HistoryRequests.Last().Before.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadOlder_WhenTheServerSaidThereIsNoMore_DoesNotAskAgain()
    {
        _connection.NextHistoryPage = new HistoryPage(1, [Message(1)], false);
        await AttachAsync();
        var requestsAfterAttach = _connection.HistoryRequests.Count;

        await _viewModel.LoadOlderCommand.ExecuteAsync(null);

        _connection.HistoryRequests.Should().HaveCount(requestsAfterAttach);
    }

    [Fact]
    public async Task MemberJoined_AddsThemAndAnnouncesIt()
    {
        await AttachAsync();

        _connection.RaiseMemberJoined(Member("Ada"));

        _viewModel.Members.Should().ContainSingle().Which.DisplayName.Should().Be("Ada");
        _viewModel.StatusMessage.Should().Be("Ada joined.");
    }

    [Fact]
    public async Task MemberJoined_ForSomeoneAlreadyListed_UpdatesInsteadOfDuplicating()
    {
        var id = Guid.NewGuid();
        await AttachAsync(Member("Ada", id));

        _connection.RaiseMemberJoined(new MemberInfo(id, "Ada Lovelace", 11, null, false, false));

        _viewModel.Members.Should().ContainSingle().Which.DisplayName.Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task MemberLeft_RemovesThem()
    {
        var id = Guid.NewGuid();
        await AttachAsync(Member("Ada", id));

        _connection.RaiseMemberLeft(id);

        _viewModel.Members.Should().BeEmpty();
        _viewModel.StatusMessage.Should().Be("Ada left.");
    }

    [Fact]
    public async Task MemberLeft_ForAnUnknownMember_IsHarmless()
    {
        await AttachAsync(Member("Ada"));

        _connection.RaiseMemberLeft(Guid.NewGuid());

        _viewModel.Members.Should().ContainSingle();
    }

    [Fact]
    public async Task MemberUpdated_ReflectsMuteAndVoiceChannelChanges()
    {
        var id = Guid.NewGuid();
        await AttachAsync(Member("Ada", id));

        _connection.RaiseMemberUpdated(new MemberInfo(id, "Ada", 10, Lobby.Id, true, false));

        var member = _viewModel.Members.Single();
        member.IsMuted.Should().BeTrue();
        member.VoiceChannelId.Should().Be(Lobby.Id);
        member.IsInVoice.Should().BeTrue();
    }

    [Fact]
    public async Task SpeakingChanged_LightsUpTheMatchingMember()
    {
        await AttachAsync(Member("Ada", ssrc: 42), Member("Grace", ssrc: 43));

        _connection.RaiseSpeakingChanged(42, true);

        _viewModel.Members.Single(m => m.Ssrc == 42).IsSpeaking.Should().BeTrue();
        _viewModel.Members.Single(m => m.Ssrc == 43).IsSpeaking.Should().BeFalse();
    }

    [Fact]
    public async Task LeavingVoice_AlsoClearsTheSpeakingIndicator()
    {
        var id = Guid.NewGuid();
        await AttachAsync(new MemberInfo(id, "Ada", 42, Lobby.Id, false, false));
        _connection.RaiseSpeakingChanged(42, true);

        _connection.RaiseMemberUpdated(new MemberInfo(id, "Ada", 42, null, false, false));

        _viewModel.Members.Single().IsSpeaking.Should().BeFalse();
    }

    [Fact]
    public async Task JoinVoice_TellsTheServerAndTracksTheActiveChannel()
    {
        await AttachAsync();

        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        _connection.JoinedVoiceChannels.Should().Equal([Lobby.Id]);
        _viewModel.ActiveVoiceChannelId.Should().Be(Lobby.Id);
    }

    [Fact]
    public async Task JoinVoice_ShowsYouUnderTheChannelYouJoined()
    {
        await AttachAsync();

        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        var lobby = _viewModel.VoiceChannels.Single();
        lobby.IsJoined.Should().BeTrue();
        lobby.Occupants.Should().ContainSingle().Which.DisplayName.Should().Be("Me");
    }

    [Fact]
    public async Task JoinVoice_WhenUdpIsBlocked_DoesNotLeaveYouListedInTheChannel()
    {
        await AttachAsync();
        _voice.StartSucceeds = false;

        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        _viewModel.VoiceChannels.Single().Occupants.Should().BeEmpty();
        _viewModel.VoiceChannels.Single().IsJoined.Should().BeFalse();
    }

    [Fact]
    public async Task MemberUpdated_MovesThemBetweenVoiceChannels()
    {
        var secondLobby = new ChannelInfo(4, "Gaming", ChannelKind.Voice, 1);
        var id = Guid.NewGuid();
        await AttachAsync(new MemberInfo(id, "Ada", 10, Lobby.Id, false, false));
        _connection.RaiseChannelsUpdated([General, OffTopic, Lobby, secondLobby]);

        _connection.RaiseMemberUpdated(new MemberInfo(id, "Ada", 10, secondLobby.Id, false, false));

        _viewModel.VoiceChannels.Single(c => c.Name == "Lobby").Occupants.Should().BeEmpty();
        _viewModel.VoiceChannels.Single(c => c.Name == "Gaming").Occupants
            .Should().ContainSingle().Which.DisplayName.Should().Be("Ada");
    }

    [Fact]
    public async Task MemberLeft_RemovesThemFromTheVoiceChannelRoster()
    {
        var id = Guid.NewGuid();
        await AttachAsync(new MemberInfo(id, "Ada", 10, Lobby.Id, false, false));

        _connection.RaiseMemberLeft(id);

        _viewModel.VoiceChannels.Single().Occupants.Should().BeEmpty();
    }

    [Fact]
    public async Task SpeakingChanged_LightsTheOccupantRowNotACopy()
    {
        await AttachAsync(new MemberInfo(Guid.NewGuid(), "Ada", 42, Lobby.Id, false, false));

        _connection.RaiseSpeakingChanged(42, true);

        _viewModel.VoiceChannels.Single().Occupants.Single().IsSpeaking.Should().BeTrue();
    }

    [Fact]
    public async Task JoinVoice_StartsTheMediaPlaneAtTheAddressTheServerHandedOut()
    {
        var session = await AttachAsync();

        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        var start = _voice.StartCalls.Should().ContainSingle().Which;
        start.Relay.Should().Be(new IPEndPoint(IPAddress.Loopback, session.VoicePort));
        start.Token.Should().Be(session.VoiceToken);
        start.Ssrc.Should().Be(session.Ssrc);
    }

    [Fact]
    public async Task JoinVoice_WhenUdpIsBlocked_BacksOutAndExplainsWhy()
    {
        // Chat is still usable, so the member list must not claim the user is in voice.
        await AttachAsync();
        _voice.StartSucceeds = false;

        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        _viewModel.ActiveVoiceChannelId.Should().BeNull();
        _connection.LeaveVoiceCalls.Should().Be(1);
        _viewModel.StatusMessage.Should().Contain("UDP 5001");
    }

    [Fact]
    public async Task JoinVoice_OnATextChannel_IsIgnored()
    {
        await AttachAsync();

        await _viewModel.JoinVoiceCommand.ExecuteAsync(General);

        _connection.JoinedVoiceChannels.Should().BeEmpty();
        _viewModel.ActiveVoiceChannelId.Should().BeNull();
        _voice.StartCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinVoice_WhileAlreadyInAChannel_ReusesTheOpenSession()
    {
        // The relay routes by channel, so switching channels is a hub concern only.
        await AttachAsync();
        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        _voice.StartCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task LeaveVoice_ClearsTheActiveChannelAndStopsAudio()
    {
        await AttachAsync();
        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        await _viewModel.LeaveVoiceCommand.ExecuteAsync(null);

        _connection.LeaveVoiceCalls.Should().Be(1);
        _viewModel.ActiveVoiceChannelId.Should().BeNull();
        _voice.IsActive.Should().BeFalse();
        _viewModel.VoiceChannels.Single().Occupants.Should().BeEmpty();
        _viewModel.VoiceChannels.Single().IsJoined.Should().BeFalse();
    }

    [Fact]
    public async Task VoiceFailure_IsSurfacedWithoutTearingDownTheChat()
    {
        await AttachAsync();
        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        _voice.RaiseFailed(new InvalidOperationException("The headset was unplugged."));

        _viewModel.StatusMessage.Should().Contain("The headset was unplugged.");
        _viewModel.ConnectionState.Should().Be(ClientConnectionState.Connected);
    }

    [Fact]
    public async Task ToggleMute_PublishesTheNewStateToTheServer()
    {
        await AttachAsync();

        await _viewModel.ToggleMuteCommand.ExecuteAsync(null);
        await _viewModel.ToggleMuteCommand.ExecuteAsync(null);

        _connection.MuteCalls.Should().Equal([true, false]);
        _viewModel.IsMuted.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleMute_AlsoGatesTheMicrophoneLocally()
    {
        // Relying on the server to stop the audio would still leak a frame or two.
        await AttachAsync();

        await _viewModel.ToggleMuteCommand.ExecuteAsync(null);

        _voice.IsMuted.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleMute_ShowsOnYourOccupantRow()
    {
        await AttachAsync();
        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        await _viewModel.ToggleMuteCommand.ExecuteAsync(null);

        _viewModel.VoiceChannels.Single().Occupants.Single().IsMuted.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleDeafen_AlsoSilencesPlaybackLocally()
    {
        await AttachAsync();

        await _viewModel.ToggleDeafenCommand.ExecuteAsync(null);

        _voice.IsDeafened.Should().BeTrue();
        _voice.IsMuted.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleDeafen_AlsoMutes()
    {
        // Hearing nobody while still transmitting is never what the user meant.
        await AttachAsync();

        await _viewModel.ToggleDeafenCommand.ExecuteAsync(null);

        _viewModel.IsDeafened.Should().BeTrue();
        _viewModel.IsMuted.Should().BeTrue();
        _connection.MuteCalls.Should().Equal([true]);
        _connection.DeafenCalls.Should().Equal([true]);
    }

    [Fact]
    public async Task Undeafening_LeavesTheMicrophoneMuted()
    {
        await AttachAsync();
        await _viewModel.ToggleDeafenCommand.ExecuteAsync(null);

        await _viewModel.ToggleDeafenCommand.ExecuteAsync(null);

        _viewModel.IsDeafened.Should().BeFalse();
        _viewModel.IsMuted.Should().BeTrue();
        _connection.DeafenCalls.Should().Equal([true, false]);
    }

    [Fact]
    public async Task ChannelsUpdated_RefreshesBothLists()
    {
        await AttachAsync();

        _connection.RaiseChannelsUpdated([General, new ChannelInfo(9, "New voice", ChannelKind.Voice, 5)]);

        _viewModel.TextChannels.Should().ContainSingle();
        _viewModel.VoiceChannels.Should().ContainSingle().Which.Name.Should().Be("New voice");
    }

    [Fact]
    public async Task ConnectionDropping_ShowsThatItIsReconnecting()
    {
        await AttachAsync();

        _connection.State = ClientConnectionState.Reconnecting;

        _viewModel.ConnectionState.Should().Be(ClientConnectionState.Reconnecting);
        _viewModel.StatusMessage.Should().Be("Connection lost. Reconnecting...");
    }

    [Fact]
    public async Task ServerNotice_IsSurfacedToTheUser()
    {
        await AttachAsync();

        _connection.RaiseServerNotice("The server is restarting.");

        _viewModel.StatusMessage.Should().Be("The server is restarting.");
    }

    [Fact]
    public async Task Disconnect_ClosesTheConnectionAndSignalsTheShell()
    {
        await AttachAsync();
        var raised = false;
        _viewModel.Disconnected += (_, _) => raised = true;

        await _viewModel.DisconnectCommand.ExecuteAsync(null);

        _connection.DisconnectCalls.Should().Be(1);
        raised.Should().BeTrue();
    }

    [Fact]
    public async Task Disconnect_AlsoTearsDownTheMediaPlane()
    {
        await AttachAsync();
        await _viewModel.JoinVoiceCommand.ExecuteAsync(Lobby);

        await _viewModel.DisconnectCommand.ExecuteAsync(null);

        _voice.IsActive.Should().BeFalse();
        _viewModel.ActiveVoiceChannelId.Should().BeNull();
    }

    [Fact]
    public async Task Attach_HandsTheSavedAudioSettingsToTheVoiceSession()
    {
        _profileStore.Document.Audio.JitterBufferFrames = 5;

        await AttachAsync();

        _voice.Settings.JitterBufferFrames.Should().Be(5);
    }

    [Fact]
    public async Task OpenSettings_AsksTheShellToShowTheAudioPage()
    {
        await AttachAsync();
        var raised = false;
        _viewModel.SettingsRequested += (_, _) => raised = true;

        _viewModel.OpenSettingsCommand.Execute(null);

        raised.Should().BeTrue();
    }
}
