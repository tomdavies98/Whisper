using FluentAssertions;
using Whisper.Shared;
using Xunit;

namespace Whisper.Tests.Protocol;

public class VoicePacketTests
{
    private static byte[] SamplePayload(int length = 64)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)(i * 7 + 3);
        }

        return payload;
    }

    [Fact]
    public void AudioHeaderSize_IsThirteenBytes()
    {
        // Pinned deliberately: changing the header silently breaks every deployed client.
        VoicePacket.AudioHeaderSize.Should().Be(13);
    }

    [Fact]
    public void WriteAudio_ThenTryReadAudio_RoundTripsEveryField()
    {
        var payload = SamplePayload();
        var buffer = new byte[VoicePacket.MaxPacketSize];

        var written = VoicePacket.WriteAudio(buffer, ssrc: 0xDEADBEEF, sequence: 4242, timestampSamples: 960_000, payload);

        written.Should().Be(VoicePacket.AudioHeaderSize + payload.Length);

        VoicePacket.TryReadAudio(buffer.AsSpan(0, written), out var header, out var readPayload)
            .Should().BeTrue();

        header.Ssrc.Should().Be(0xDEADBEEF);
        header.Sequence.Should().Be(4242);
        header.TimestampSamples.Should().Be(960_000);
        header.PayloadLength.Should().Be((ushort)payload.Length);
        readPayload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void WriteAudio_UsesBigEndianFieldOrder()
    {
        var buffer = new byte[VoicePacket.MaxPacketSize];
        VoicePacket.WriteAudio(buffer, ssrc: 0x01020304, sequence: 0x0506, timestampSamples: 0x0708090A, [0xFF]);

        buffer[0].Should().Be((byte)VoiceMessageType.Audio);
        buffer[1..5].Should().Equal([0x01, 0x02, 0x03, 0x04]);
        buffer[5..7].Should().Equal([0x05, 0x06]);
        buffer[7..11].Should().Equal([0x07, 0x08, 0x09, 0x0A]);
        buffer[11..13].Should().Equal([0x00, 0x01]);
        buffer[13].Should().Be(0xFF);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    public void TryReadAudio_BufferShorterThanHeader_ReturnsFalse(int length)
    {
        var buffer = new byte[VoicePacket.MaxPacketSize];
        VoicePacket.WriteAudio(buffer, 1, 1, 1, SamplePayload());

        VoicePacket.TryReadAudio(buffer.AsSpan(0, length), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadAudio_PayloadTruncated_ReturnsFalse()
    {
        var payload = SamplePayload();
        var buffer = new byte[VoicePacket.MaxPacketSize];
        var written = VoicePacket.WriteAudio(buffer, 1, 1, 1, payload);

        VoicePacket.TryReadAudio(buffer.AsSpan(0, written - 1), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadAudio_DeclaredPayloadLengthBeyondBuffer_ReturnsFalse()
    {
        var buffer = new byte[VoicePacket.AudioHeaderSize + 4];
        buffer[0] = (byte)VoiceMessageType.Audio;
        buffer[11] = 0x04;
        buffer[12] = 0x00; // claims 1024 bytes of payload while carrying 4

        VoicePacket.TryReadAudio(buffer, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadAudio_ZeroLengthPayload_ReturnsFalse()
    {
        var buffer = new byte[VoicePacket.AudioHeaderSize];
        buffer[0] = (byte)VoiceMessageType.Audio;

        VoicePacket.TryReadAudio(buffer, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadAudio_WrongMessageType_ReturnsFalse()
    {
        var buffer = new byte[VoicePacket.MaxPacketSize];
        VoicePacket.WriteAudio(buffer, 1, 1, 1, SamplePayload());
        buffer[0] = (byte)VoiceMessageType.Keepalive;

        VoicePacket.TryReadAudio(buffer, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void WriteAudio_PayloadLargerThanOpusMaximum_Throws()
    {
        var buffer = new byte[AudioFormat.MaxOpusPayload + 64];
        var oversized = new byte[AudioFormat.MaxOpusPayload + 1];

        var write = () => VoicePacket.WriteAudio(buffer, 1, 1, 1, oversized);

        write.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryPeekType_UnknownTypeByte_ReturnsFalse()
    {
        VoicePacket.TryPeekType([0x7F], out _).Should().BeFalse();
    }

    [Fact]
    public void TryPeekType_EmptyBuffer_ReturnsFalse()
    {
        VoicePacket.TryPeekType([], out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(VoiceMessageType.Handshake)]
    [InlineData(VoiceMessageType.HandshakeAck)]
    [InlineData(VoiceMessageType.Audio)]
    [InlineData(VoiceMessageType.Keepalive)]
    public void TryPeekType_KnownTypeByte_ReturnsType(VoiceMessageType expected)
    {
        VoicePacket.TryPeekType([(byte)expected], out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Handshake_RoundTripsVoiceToken()
    {
        var token = Guid.NewGuid();
        var buffer = new byte[VoicePacket.HandshakeSize];

        VoicePacket.WriteHandshake(buffer, token).Should().Be(VoicePacket.HandshakeSize);
        VoicePacket.TryReadHandshake(buffer, out var readToken).Should().BeTrue();
        readToken.Should().Be(token);
    }

    [Fact]
    public void TryReadHandshake_TruncatedToken_ReturnsFalse()
    {
        var buffer = new byte[VoicePacket.HandshakeSize];
        VoicePacket.WriteHandshake(buffer, Guid.NewGuid());

        VoicePacket.TryReadHandshake(buffer.AsSpan(0, VoicePacket.HandshakeSize - 1), out _).Should().BeFalse();
    }

    [Fact]
    public void HandshakeAck_RoundTripsSsrc()
    {
        var buffer = new byte[VoicePacket.HandshakeAckSize];

        VoicePacket.WriteHandshakeAck(buffer, 0x11223344).Should().Be(VoicePacket.HandshakeAckSize);
        VoicePacket.TryReadHandshakeAck(buffer, out var ssrc).Should().BeTrue();
        ssrc.Should().Be(0x11223344);
    }

    [Fact]
    public void Keepalive_RoundTripsSsrc()
    {
        var buffer = new byte[VoicePacket.KeepaliveSize];

        VoicePacket.WriteKeepalive(buffer, 7).Should().Be(VoicePacket.KeepaliveSize);
        VoicePacket.TryReadKeepalive(buffer, out var ssrc).Should().BeTrue();
        ssrc.Should().Be(7u);
    }

    [Fact]
    public void TryReadKeepalive_AudioPacket_ReturnsFalse()
    {
        var buffer = new byte[VoicePacket.MaxPacketSize];
        VoicePacket.WriteAudio(buffer, 1, 1, 1, SamplePayload());

        VoicePacket.TryReadKeepalive(buffer, out _).Should().BeFalse();
    }
}
