using FluentAssertions;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Protocol;

public class SequenceNumberTests
{
    [Fact]
    public void Next_AtMaximum_WrapsToZero()
    {
        SequenceNumber.Next(ushort.MaxValue).Should().Be(0);
    }

    [Fact]
    public void Next_BelowMaximum_Increments()
    {
        SequenceNumber.Next(41).Should().Be(42);
    }

    [Fact]
    public void IsNewer_AcrossWrapBoundary_TreatsZeroAsNewerThanMaximum()
    {
        SequenceNumber.IsNewer(0, ushort.MaxValue).Should().BeTrue();
        SequenceNumber.IsNewer(ushort.MaxValue, 0).Should().BeFalse();
    }

    [Fact]
    public void IsNewer_WithinRange_ComparesForwards()
    {
        SequenceNumber.IsNewer(100, 99).Should().BeTrue();
        SequenceNumber.IsNewer(99, 100).Should().BeFalse();
        SequenceNumber.IsNewer(100, 100).Should().BeFalse();
    }

    [Fact]
    public void Distance_AcrossWrapBoundary_StaysSmall()
    {
        SequenceNumber.Distance(2, ushort.MaxValue).Should().Be(3);
        SequenceNumber.Distance(ushort.MaxValue, 2).Should().Be(-3);
    }
}
