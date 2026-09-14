using CommunityToolkit.Mvvm.ComponentModel;
using Whisper.Shared.Contracts;

namespace Whisper.Client.ViewModels;

public sealed partial class MemberViewModel : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private int? _voiceChannelId;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isDeafened;

    [ObservableProperty]
    private bool _isSpeaking;

    public MemberViewModel(MemberInfo member)
    {
        ClientId = member.ClientId;
        Ssrc = member.Ssrc;
        Update(member);
    }

    public Guid ClientId { get; }

    public uint Ssrc { get; private set; }

    public bool IsInVoice => VoiceChannelId is not null;

    public void Update(MemberInfo member)
    {
        Ssrc = member.Ssrc;
        DisplayName = member.DisplayName;
        VoiceChannelId = member.VoiceChannelId;
        IsMuted = member.IsMuted;
        IsDeafened = member.IsDeafened;
    }

    partial void OnVoiceChannelIdChanged(int? value)
    {
        OnPropertyChanged(nameof(IsInVoice));

        if (value is null)
        {
            IsSpeaking = false;
        }
    }
}
