using Microsoft.AspNetCore.SignalR;
using Whisper.Server.Hubs;
using Whisper.Shared;

namespace Whisper.Server.Voice;

/// <summary>
/// The relay's only route back to connected clients. Keeping it behind an interface means
/// the relay can be unit tested without standing up SignalR.
/// </summary>
public interface IVoiceNotifier
{
    Task SpeakingChanged(uint ssrc, bool isSpeaking);

    Task MemberUpdated(VoiceSession session);
}

public sealed class HubVoiceNotifier(IHubContext<WhisperHub, IWhisperClient> hub) : IVoiceNotifier
{
    public Task SpeakingChanged(uint ssrc, bool isSpeaking) =>
        hub.Clients.All.SpeakingChanged(ssrc, isSpeaking);

    public Task MemberUpdated(VoiceSession session) =>
        hub.Clients.All.MemberUpdated(session.ToMemberInfo());
}
