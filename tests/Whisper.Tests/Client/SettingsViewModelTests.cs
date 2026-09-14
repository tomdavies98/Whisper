using FluentAssertions;
using NSubstitute;
using Whisper.Client.Audio;
using Whisper.Client.Models;
using Whisper.Client.ViewModels;
using Xunit;

namespace Whisper.Tests.Client;

public class SettingsViewModelTests
{
    private static readonly AudioDeviceInfo DefaultMic = new("mic-default", "Built-in microphone", true);
    private static readonly AudioDeviceInfo Headset = new("mic-headset", "Headset", false);
    private static readonly AudioDeviceInfo Speakers = new("out-speakers", "Speakers", true);
    private static readonly AudioDeviceInfo Monitor = new("out-monitor", "Monitor", false);

    private readonly IAudioDeviceProvider _devices = Substitute.For<IAudioDeviceProvider>();
    private readonly FakeVoiceSession _voice = new();
    private readonly InMemoryProfileStore _profileStore = new();
    private readonly SettingsViewModel _viewModel;

    public SettingsViewModelTests()
    {
        _devices.GetInputDevices().Returns([DefaultMic, Headset]);
        _devices.GetOutputDevices().Returns([Speakers, Monitor]);

        _viewModel = new SettingsViewModel(_devices, _voice, _profileStore, new ImmediateUiDispatcher());
    }

    [Fact]
    public void Load_ListsTheAvailableDevices()
    {
        _viewModel.Load();

        _viewModel.InputDevices.Should().Equal(DefaultMic, Headset);
        _viewModel.OutputDevices.Should().Equal(Speakers, Monitor);
    }

    [Fact]
    public void Load_SelectsThePreviouslySavedDevices()
    {
        _profileStore.Document.Audio.InputDeviceId = Headset.Id;
        _profileStore.Document.Audio.OutputDeviceId = Monitor.Id;

        _viewModel.Load();

        _viewModel.SelectedInputDevice.Should().Be(Headset);
        _viewModel.SelectedOutputDevice.Should().Be(Monitor);
    }

    [Fact]
    public void Load_WhenTheSavedDeviceIsGone_FallsBackToTheSystemDefault()
    {
        // Devices come and go; a missing headset must not leave the picker empty.
        _profileStore.Document.Audio.InputDeviceId = "a-device-that-was-unplugged";

        _viewModel.Load();

        _viewModel.SelectedInputDevice.Should().Be(DefaultMic);
    }

    [Fact]
    public void Load_RestoresTheSavedAudioPreferences()
    {
        _profileStore.Document.Audio.UsePushToTalk = true;
        _profileStore.Document.Audio.VoiceActivityThresholdDb = -33f;
        _profileStore.Document.Audio.JitterBufferFrames = 7;
        _profileStore.Document.Audio.InputGain = 1.8f;

        _viewModel.Load();

        _viewModel.UsePushToTalk.Should().BeTrue();
        _viewModel.VoiceActivityThresholdDb.Should().Be(-33f);
        _viewModel.JitterBufferFrames.Should().Be(7);
        _viewModel.InputGain.Should().Be(1.8f);
    }

    [Fact]
    public void Load_AfterAnEarlierSave_ShowsTheSavedValuesRatherThanStaleOnes()
    {
        _viewModel.Load();
        _viewModel.JitterBufferFrames = 6;
        _viewModel.ApplyCommand.Execute(null);

        _viewModel.JitterBufferFrames = 2;
        _viewModel.Load();

        _viewModel.JitterBufferFrames.Should().Be(6);
    }

    [Fact]
    public void Load_WhenTheDeviceListCannotBeRead_ReportsItInsteadOfThrowing()
    {
        _devices.GetInputDevices().Returns(_ => throw new InvalidOperationException("The audio service is down."));

        var load = () => _viewModel.Load();

        load.Should().NotThrow();
        _viewModel.StatusMessage.Should().Contain("The audio service is down.");
    }

    [Fact]
    public void Apply_PersistsTheChoices()
    {
        _viewModel.Load();
        _viewModel.SelectedInputDevice = Headset;
        _viewModel.UsePushToTalk = true;
        _viewModel.JitterBufferFrames = 5;

        _viewModel.ApplyCommand.Execute(null);

        var saved = _profileStore.Document.Audio;
        saved.InputDeviceId.Should().Be(Headset.Id);
        saved.UsePushToTalk.Should().BeTrue();
        saved.JitterBufferFrames.Should().Be(5);
    }

    [Fact]
    public void Apply_PushesTheChoicesToTheLiveVoiceSession()
    {
        _viewModel.Load();
        _viewModel.VoiceActivityThresholdDb = -30f;

        _viewModel.ApplyCommand.Execute(null);

        _voice.Settings.VoiceActivityThresholdDb.Should().Be(-30f);
    }

    [Fact]
    public void Apply_WhileInACall_SaysDeviceChangesWaitForTheNextCall()
    {
        _viewModel.Load();
        _voice.StartAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5001), Guid.NewGuid(), 1);

        _viewModel.ApplyCommand.Execute(null);

        _viewModel.StatusMessage.Should().Contain("next time you join");
    }

    [Fact]
    public void InputLevel_TracksTheVoiceSessionMeter()
    {
        _viewModel.Load();

        _voice.RaiseInputLevel(0.42f);

        _viewModel.InputLevel.Should().Be(0.42f);
    }

    [Fact]
    public void JitterBufferLabel_ShowsTheLatencyTheChoiceCosts()
    {
        _viewModel.JitterBufferFrames = 4;

        _viewModel.JitterBufferLabel.Should().Be("80 ms");
    }

    [Fact]
    public void Close_SavesAndSignalsTheShell()
    {
        _viewModel.Load();
        _viewModel.InputGain = 2f;
        var closed = false;
        _viewModel.Closed += (_, _) => closed = true;

        _viewModel.CloseCommand.Execute(null);

        closed.Should().BeTrue();
        _profileStore.Document.Audio.InputGain.Should().Be(2f);
    }
}
