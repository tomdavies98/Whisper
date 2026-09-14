using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Whisper.Shared.Contracts;

namespace Whisper.Client.ViewModels;

/// <summary>
/// A voice channel plus whoever is in it right now. Occupants are the same
/// <see cref="MemberViewModel"/> instances as the server-wide member list, so speaking
/// and mute indicators update in both places without a second copy of the state.
/// </summary>
public sealed partial class VoiceChannelViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isJoined;

    public VoiceChannelViewModel(ChannelInfo channel) => Channel = channel;

    public ChannelInfo Channel { get; }

    public int Id => Channel.Id;

    public string Name => Channel.Name;

    public ObservableCollection<MemberViewModel> Occupants { get; } = [];
}
