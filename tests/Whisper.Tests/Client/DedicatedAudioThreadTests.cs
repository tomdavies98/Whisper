using FluentAssertions;
using Whisper.Client.Infrastructure;
using Xunit;

namespace Whisper.Tests.Client;

public class DedicatedAudioThreadTests
{
    [Fact]
    public async Task InvokeAsync_RunsEveryCallOnTheSameThread()
    {
        using var audio = new DedicatedAudioThread();
        var first = 0;
        var second = 0;

        await audio.InvokeAsync(() => first = Environment.CurrentManagedThreadId);
        await audio.InvokeAsync(() => second = Environment.CurrentManagedThreadId);

        first.Should().Be(second);
        first.Should().NotBe(Environment.CurrentManagedThreadId);
    }
}
