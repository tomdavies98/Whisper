using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Whisper.Client.Audio;
using Whisper.Client.Infrastructure;
using Whisper.Client.Models;
using Whisper.Client.Services;

namespace Whisper.Client.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAudioDeviceProvider _devices;
    private readonly IVoiceSession _voice;
    private readonly IProfileStore _profileStore;
    private readonly IUiDispatcher _ui;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedInputDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedOutputDevice;

    [ObservableProperty]
    private bool _usePushToTalk;

    [ObservableProperty]
    private float _voiceActivityThresholdDb = -45f;

    [ObservableProperty]
    private int _jitterBufferFrames = 3;

    [ObservableProperty]
    private float _inputGain = 1f;

    [ObservableProperty]
    private float _inputLevel;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    private bool _loading;
    private bool _meterEnabled;

    public SettingsViewModel(
        IAudioDeviceProvider devices,
        IVoiceSession voice,
        IProfileStore profileStore,
        IUiDispatcher ui)
    {
        _devices = devices;
        _voice = voice;
        _profileStore = profileStore;
        _ui = ui;

        _voice.InputLevelChanged += (_, level) => _ui.Post(() => InputLevel = level);
    }

    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = [];

    /// <summary>Latency added by the jitter buffer, so the trade-off is visible.</summary>
    public string JitterBufferLabel => $"{JitterBufferFrames * Whisper.Shared.AudioFormat.FrameMilliseconds} ms";

    /// <summary>
    /// Where the voice-activation threshold sits on the same 0..1 scale as the meter,
    /// so a user can see whether their speech would open the gate.
    /// </summary>
    public float ThresholdMeterPosition => VoiceActivityGate.ToDisplayLevel(VoiceActivityThresholdDb);

    public bool ShowThresholdMarker => !UsePushToTalk;

    public event EventHandler? Closed;

    /// <summary>
    /// Reads straight from the store rather than taking a caller-supplied snapshot, so
    /// reopening the page after an earlier save never shows stale values.
    /// </summary>
    public void Load()
    {
        var settings = _profileStore.Load().Audio;

        _loading = true;
        _meterEnabled = false;

        try
        {
            RefreshDevicesCommand.Execute(null);

            SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Id == settings.InputDeviceId)
                ?? InputDevices.FirstOrDefault(d => d.IsDefault)
                ?? InputDevices.FirstOrDefault();

            SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == settings.OutputDeviceId)
                ?? OutputDevices.FirstOrDefault(d => d.IsDefault)
                ?? OutputDevices.FirstOrDefault();

            UsePushToTalk = settings.UsePushToTalk;
            VoiceActivityThresholdDb = settings.VoiceActivityThresholdDb;
            JitterBufferFrames = settings.JitterBufferFrames;
            InputGain = settings.InputGain;
            InputLevel = 0;
            ApplyDraftToVoice();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Opens the microphone after the settings page has rendered. Starting capture during
    /// <see cref="Load"/> deadlocked the UI: WASAPI/WaveIn on the audio thread waited for
    /// the STA dispatcher, which was still inside the navigation click.
    /// </summary>
    public async Task StartMeterAsync()
    {
        if (_meterEnabled)
        {
            return;
        }

        _meterEnabled = true;
        await ListenAsync();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        InputDevices.Clear();
        OutputDevices.Clear();

        try
        {
            foreach (var device in _devices.GetInputDevices())
            {
                InputDevices.Add(device);
            }

            foreach (var device in _devices.GetOutputDevices())
            {
                OutputDevices.Add(device);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not list audio devices: {ex.Message}";
        }
    }

    /// <summary>Writes the settings back to disk and applies them to the live session.</summary>
    [RelayCommand]
    private void Apply()
    {
        var document = _profileStore.Load();

        document.Audio.InputDeviceId = SelectedInputDevice?.Id;
        document.Audio.OutputDeviceId = SelectedOutputDevice?.Id;
        document.Audio.UsePushToTalk = UsePushToTalk;
        document.Audio.VoiceActivityThresholdDb = VoiceActivityThresholdDb;
        document.Audio.JitterBufferFrames = JitterBufferFrames;
        document.Audio.InputGain = InputGain;

        _profileStore.Save(document);
        _voice.ApplySettings(document.Audio);

        StatusMessage = "Saved. Speak to test the microphone.";
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        Apply();

        try
        {
            await _voice.StopInputMeterAsync();
        }
        catch (Exception)
        {
            // Closing settings must still return to the session.
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyDraftToVoice() => _voice.ApplySettings(new AudioSettings
    {
        InputDeviceId = SelectedInputDevice?.Id,
        OutputDeviceId = SelectedOutputDevice?.Id,
        UsePushToTalk = UsePushToTalk,
        PushToTalkKey = _voice.Settings.PushToTalkKey,
        VoiceActivityThresholdDb = VoiceActivityThresholdDb,
        JitterBufferFrames = JitterBufferFrames,
        InputGain = InputGain,
    });

    private async Task ListenAsync()
    {
        try
        {
            await _voice.StartInputMeterAsync();
        }
        catch (Exception ex)
        {
            _ui.Post(() => StatusMessage = $"Could not open the microphone: {ex.Message}");
        }
    }

    partial void OnJitterBufferFramesChanged(int value) => OnPropertyChanged(nameof(JitterBufferLabel));

    partial void OnVoiceActivityThresholdDbChanged(float value)
    {
        OnPropertyChanged(nameof(ThresholdMeterPosition));

        if (!_loading)
        {
            ApplyDraftToVoice();
        }
    }

    partial void OnUsePushToTalkChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowThresholdMarker));

        if (!_loading)
        {
            ApplyDraftToVoice();
        }
    }

    partial void OnInputGainChanged(float value)
    {
        if (!_loading)
        {
            ApplyDraftToVoice();
        }
    }

    partial void OnSelectedInputDeviceChanged(AudioDeviceInfo? value)
    {
        if (_loading || !_meterEnabled)
        {
            return;
        }

        ApplyDraftToVoice();
        _ = ListenAsync();
    }
}
