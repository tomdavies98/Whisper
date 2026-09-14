using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Whisper.Client.Audio;
using Xunit;

namespace Whisper.Tests.Client;

public class VoiceActivityGateTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly VoiceActivityGate _gate;

    public VoiceActivityGateTests() => _gate = new VoiceActivityGate(_time) { ThresholdDb = -45f };

    [Fact]
    public void Process_WithSpeechLevelAudio_Transmits()
    {
        _gate.Process(AudioSignals.Sine(amplitude: 0.5)).Should().BeTrue();
        _gate.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Process_WithDigitalSilence_DoesNotTransmit()
    {
        _gate.Process(AudioSignals.Silence()).Should().BeFalse();
    }

    [Fact]
    public void Process_WithQuietRoomNoise_DoesNotTransmit()
    {
        // The whole point of the gate is not to send a keyboard-and-fan-noise stream.
        _gate.Process(AudioSignals.RoomNoise()).Should().BeFalse();
    }

    [Fact]
    public void Process_JustBelowTheThreshold_DoesNotTransmit()
    {
        _gate.ThresholdDb = -20f;

        _gate.Process(AudioSignals.Sine(amplitude: 0.02)).Should().BeFalse();
    }

    [Fact]
    public void Process_AfterSpeechStops_StaysOpenForTheHangoverWindow()
    {
        _gate.Hangover = TimeSpan.FromMilliseconds(300);
        _gate.Process(AudioSignals.Sine());

        _time.Advance(TimeSpan.FromMilliseconds(200));

        _gate.Process(AudioSignals.Silence()).Should().BeTrue("trailing consonants would otherwise be clipped");
    }

    [Fact]
    public void Process_AfterTheHangoverExpires_Closes()
    {
        _gate.Hangover = TimeSpan.FromMilliseconds(300);
        _gate.Process(AudioSignals.Sine());

        _time.Advance(TimeSpan.FromMilliseconds(400));

        _gate.Process(AudioSignals.Silence()).Should().BeFalse();
        _gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Process_ContinuousSpeech_KeepsRefreshingTheHangover()
    {
        _gate.Hangover = TimeSpan.FromMilliseconds(300);

        for (var i = 0; i < 50; i++)
        {
            _gate.Process(AudioSignals.Sine(startSample: i * 960)).Should().BeTrue();
            _time.Advance(TimeSpan.FromMilliseconds(20));
        }

        _time.Advance(TimeSpan.FromMilliseconds(400));
        _gate.Process(AudioSignals.Silence()).Should().BeFalse();
    }

    [Fact]
    public void LastLevel_TracksTheMeasuredInputForTheMeter()
    {
        _gate.Process(AudioSignals.Sine(amplitude: 0.5));
        var loud = _gate.LastLevel;

        _gate.Process(AudioSignals.Sine(amplitude: 0.01));
        var quiet = _gate.LastLevel;

        loud.Should().BeInRange(0f, 1f);
        quiet.Should().BeInRange(0f, 1f);
        loud.Should().BeGreaterThan(quiet);
    }

    [Fact]
    public void LastLevel_ForDigitalSilence_IsZero()
    {
        _gate.Process(AudioSignals.Silence());

        _gate.LastLevel.Should().Be(0f);
        _gate.LastLevelDb.Should().Be(float.NegativeInfinity);
    }

    [Fact]
    public void MeasureDb_OfAHalfScaleSine_IsAboutMinusNineDecibels()
    {
        // RMS of a sine at 0.5 amplitude is 0.354, which is -9 dBFS. Anchors the scale so
        // the default threshold stays meaningful.
        VoiceActivityGate.MeasureDb(AudioSignals.Sine(amplitude: 0.5))
            .Should().BeApproximately(-9f, 0.5f);
    }

    [Fact]
    public void Reset_ClosesTheGateAndClearsTheMeter()
    {
        _gate.Process(AudioSignals.Sine());

        _gate.Reset();

        _gate.IsOpen.Should().BeFalse();
        _gate.LastLevel.Should().Be(0f);
        _gate.Process(AudioSignals.Silence()).Should().BeFalse();
    }

    [Fact]
    public void ToDisplayLevel_MapsSilenceToTheLeftAndFullScaleToTheRight()
    {
        VoiceActivityGate.ToDisplayLevel(float.NegativeInfinity).Should().Be(0f);
        VoiceActivityGate.ToDisplayLevel(-70f).Should().Be(0f);
        VoiceActivityGate.ToDisplayLevel(-35f).Should().BeApproximately(0.5f, 0.001f);
        VoiceActivityGate.ToDisplayLevel(0f).Should().Be(1f);
    }

    [Fact]
    public void Process_UsesPeakForTheMeterSoQuietSpeechStillMovesIt()
    {
        _gate.Process(AudioSignals.Sine(amplitude: 0.003));

        _gate.LastLevel.Should().BeGreaterThan(0.2f);
        _gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Process_OpensWhenPeakCrossesTheConfiguredThreshold()
    {
        _gate.ThresholdDb = -40f;

        _gate.Process(AudioSignals.Sine(amplitude: 0.02)).Should().BeTrue();
    }

    [Fact]
    public void Process_StaysClosedWhenPeakIsBelowTheConfiguredThreshold()
    {
        _gate.ThresholdDb = -30f;

        _gate.Process(AudioSignals.Sine(amplitude: 0.02)).Should().BeFalse();
    }
}
