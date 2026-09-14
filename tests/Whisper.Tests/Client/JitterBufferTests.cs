using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Whisper.Client.Audio;
using Xunit;

namespace Whisper.Tests.Client;

public class JitterBufferTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly JitterBuffer _buffer;

    public JitterBufferTests() => _buffer = new JitterBuffer(_time, targetFrames: 3);

    /// <summary>
    /// The payload carries its own sequence number so drained frames can be identified,
    /// and a null stands for a gap the decoder is expected to conceal.
    /// </summary>
    private static byte[] Payload(ushort sequence) => BitConverter.GetBytes(sequence);

    private void Add(params ushort[] sequences)
    {
        foreach (var sequence in sequences)
        {
            _buffer.Add(sequence, Payload(sequence));
        }
    }

    private int?[] Drain(int count)
    {
        var drained = new int?[count];

        for (var i = 0; i < count; i++)
        {
            drained[i] = _buffer.TryDequeue(out var frame) && frame.Payload is { } payload
                ? BitConverter.ToUInt16(payload)
                : null;
        }

        return drained;
    }

    [Fact]
    public void TryDequeue_BeforeTheTargetDepthIsReached_YieldsNothing()
    {
        // Playing the first frame the instant it lands leaves no slack for the next one.
        Add(1, 2);

        _buffer.TryDequeue(out _).Should().BeFalse();
        _buffer.IsPrimed.Should().BeFalse();
    }

    [Fact]
    public void TryDequeue_OnceTheTargetDepthIsReached_StartsPlayback()
    {
        Add(1, 2, 3);

        _buffer.TryDequeue(out var frame).Should().BeTrue();
        frame.Sequence.Should().Be(1);
    }

    [Fact]
    public void Add_OutOfOrder_StillPlaysInOrder()
    {
        Add(3, 1, 2);

        Drain(3).Should().Equal(new int?[] { 1, 2, 3 });
    }

    [Fact]
    public void TryDequeue_WithAFrameMissing_ReportsAGapSoTheDecoderCanConceal()
    {
        // Silence would click; Opus PLC needs to be told the frame is gone.
        Add(1, 2, 4);

        var frames = Drain(3);

        frames.Should().Equal(new int?[] { 1, 2, null });
        _buffer.ConcealedFrames.Should().Be(1);
    }

    [Fact]
    public void TryDequeue_AfterAConcealedGap_ResumesWithTheFrameThatDidArrive()
    {
        Add(1, 2, 4, 5);

        Drain(4).Should().Equal(new int?[] { 1, 2, null, 4 });
    }

    [Fact]
    public void Add_AFrameThatIsAlreadyPast_IsDroppedRatherThanPlayedLate()
    {
        Add(1, 2, 3);
        _buffer.TryDequeue(out _);
        _buffer.TryDequeue(out _);

        _buffer.Add(1, Payload(1)).Should().BeFalse();
        _buffer.DroppedLateFrames.Should().Be(1);
    }

    [Fact]
    public void Add_ReorderingBeforePlaybackStarts_IsNotTreatedAsLate()
    {
        // Until the buffer primes there is no playout position to be late for.
        Add(5, 4);

        _buffer.Add(3, Payload(3)).Should().BeTrue();
        _buffer.DroppedLateFrames.Should().Be(0);
        Drain(3).Should().Equal(new int?[] { 3, 4, 5 });
    }

    [Fact]
    public void Add_TheSameSequenceTwice_KeepsOnlyOne()
    {
        _buffer.Add(1, Payload(1)).Should().BeTrue();

        _buffer.Add(1, Payload(1)).Should().BeFalse();
        _buffer.Count.Should().Be(1);
    }

    [Fact]
    public void Add_UnderSteadyArrival_HoldsTheTargetDepth()
    {
        // One frame in, one frame out: the buffer should neither drain nor creep upwards.
        for (ushort sequence = 1; sequence <= 50; sequence++)
        {
            _buffer.Add(sequence, Payload(sequence));
            _time.Advance(TimeSpan.FromMilliseconds(20));

            if (_buffer.IsPrimed || _buffer.Count >= 3)
            {
                _buffer.TryDequeue(out _).Should().BeTrue();
            }
        }

        _buffer.Count.Should().Be(2);
        _buffer.DroppedLateFrames.Should().Be(0);
        _buffer.ConcealedFrames.Should().Be(0);
    }

    [Fact]
    public void Add_BeyondTheCeiling_DiscardsTheOldestInsteadOfGrowing()
    {
        _buffer.MaxFrames = 4;

        for (ushort sequence = 1; sequence <= 10; sequence++)
        {
            _buffer.Add(sequence, Payload(sequence));
        }

        _buffer.Count.Should().Be(4);
        _buffer.DroppedOverflowFrames.Should().Be(6);
    }

    [Fact]
    public void TryDequeue_WhenTheBufferRunsDry_StopsRatherThanTricklingFrames()
    {
        Add(1, 2, 3);
        Drain(3);

        _buffer.TryDequeue(out _).Should().BeFalse();
        _buffer.IsPrimed.Should().BeFalse();
    }

    [Fact]
    public void Add_AfterTheSpeakerWentQuiet_RePrimesInsteadOfReplayingStaleAudio()
    {
        Add(1, 2);
        _time.Advance(TimeSpan.FromSeconds(5));

        Add(60, 61, 62);

        Drain(3).Should().Equal(new int?[] { 60, 61, 62 });
    }

    [Fact]
    public void TryDequeue_AcrossTheSequenceWrap_KeepsPlayingInOrder()
    {
        Add(65534, 65535, 0, 1);

        Drain(4).Should().Equal(new int?[] { 65534, 65535, 0, 1 });
    }

    [Fact]
    public void Reset_ClearsTheBufferAndTheFillState()
    {
        Add(1, 2, 3);
        _buffer.TryDequeue(out _);

        _buffer.Reset();

        _buffer.Count.Should().Be(0);
        _buffer.IsPrimed.Should().BeFalse();
    }
}
