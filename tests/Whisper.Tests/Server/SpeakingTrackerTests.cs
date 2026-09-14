using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Whisper.Server.Voice;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Server;

public class SpeakingTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly SpeakingTracker _tracker;

    public SpeakingTrackerTests() => _tracker = new SpeakingTracker(_time);

    [Fact]
    public void NotePacket_FirstPacket_ReportsTheStartOfSpeech()
    {
        _tracker.NotePacket(ssrc: 1).Should().BeTrue();
        _tracker.IsSpeaking(1).Should().BeTrue();
    }

    [Fact]
    public void NotePacket_WhileAlreadySpeaking_ReportsNothingNew()
    {
        _tracker.NotePacket(1);

        _tracker.NotePacket(1).Should().BeFalse();
        _tracker.NotePacket(1).Should().BeFalse();
    }

    [Fact]
    public void CollectStopped_BeforeTheDecayWindowElapses_ReportsNothing()
    {
        _tracker.NotePacket(1);

        _time.Advance(ProtocolLimits.SpeakingDecay - TimeSpan.FromMilliseconds(10));

        _tracker.CollectStopped(ProtocolLimits.SpeakingDecay).Should().BeEmpty();
        _tracker.IsSpeaking(1).Should().BeTrue();
    }

    [Fact]
    public void CollectStopped_AfterTwoHundredMillisecondsOfSilence_ReportsTheStop()
    {
        _tracker.NotePacket(1);

        _time.Advance(ProtocolLimits.SpeakingDecay + TimeSpan.FromMilliseconds(10));

        _tracker.CollectStopped(ProtocolLimits.SpeakingDecay).Should().Equal([1u]);
        _tracker.IsSpeaking(1).Should().BeFalse();
    }

    [Fact]
    public void CollectStopped_CalledRepeatedly_ReportsEachStopOnce()
    {
        _tracker.NotePacket(1);
        _time.Advance(ProtocolLimits.SpeakingDecay * 2);

        _tracker.CollectStopped(ProtocolLimits.SpeakingDecay).Should().Equal([1u]);
        _tracker.CollectStopped(ProtocolLimits.SpeakingDecay).Should().BeEmpty();
    }

    [Fact]
    public void ContinuousSpeech_ProducesExactlyOneStartAndOneStopPerBurst()
    {
        // One second of 20 ms frames is 50 packets. The point of edge triggering is that
        // this produces two broadcasts, not a hundred.
        var transitions = 0;

        for (var i = 0; i < 50; i++)
        {
            if (_tracker.NotePacket(1))
            {
                transitions++;
            }

            _time.Advance(TimeSpan.FromMilliseconds(20));
            transitions += _tracker.CollectStopped(ProtocolLimits.SpeakingDecay).Count;
        }

        transitions.Should().Be(1);

        _time.Advance(ProtocolLimits.SpeakingDecay * 2);
        transitions += _tracker.CollectStopped(ProtocolLimits.SpeakingDecay).Count;

        transitions.Should().Be(2);
    }

    [Fact]
    public void NotePacket_AfterAPreviousBurstEnded_ReportsTheNewStart()
    {
        _tracker.NotePacket(1);
        _time.Advance(ProtocolLimits.SpeakingDecay * 2);
        _tracker.CollectStopped(ProtocolLimits.SpeakingDecay);

        _tracker.NotePacket(1).Should().BeTrue();
    }

    [Fact]
    public void CollectStopped_TracksEachSsrcIndependently()
    {
        _tracker.NotePacket(1);
        _time.Advance(ProtocolLimits.SpeakingDecay + TimeSpan.FromMilliseconds(10));
        _tracker.NotePacket(2);

        _tracker.CollectStopped(ProtocolLimits.SpeakingDecay).Should().Equal([1u]);
        _tracker.IsSpeaking(2).Should().BeTrue();
    }

    [Fact]
    public void Forget_RemovesTheSsrcEntirely()
    {
        _tracker.NotePacket(1);

        _tracker.Forget(1);

        _tracker.IsSpeaking(1).Should().BeFalse();
    }
}
