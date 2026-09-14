using System.Buffers.Binary;

namespace Whisper.Shared;

public enum VoiceMessageType : byte
{
    Handshake = 0x01,
    HandshakeAck = 0x02,
    Audio = 0x10,
    Keepalive = 0x20,
}

/// <summary>Fixed-size part of an audio packet, laid out big-endian on the wire.</summary>
public readonly record struct VoiceAudioHeader(uint Ssrc, ushort Sequence, uint TimestampSamples, ushort PayloadLength);

/// <summary>
/// Wire format for the media plane. Every reader is total: a malformed or truncated
/// datagram returns false rather than throwing, because this runs on a socket loop
/// that anyone on the network can send bytes to.
/// </summary>
public static class VoicePacket
{
    /// <summary>type(1) + ssrc(4) + sequence(2) + timestamp(4) + payloadLength(2).</summary>
    public const int AudioHeaderSize = 13;

    public const int HandshakeSize = 17;
    public const int HandshakeAckSize = 5;
    public const int KeepaliveSize = 5;

    public const int MaxPacketSize = AudioHeaderSize + AudioFormat.MaxOpusPayload;

    public static bool TryPeekType(ReadOnlySpan<byte> buffer, out VoiceMessageType type)
    {
        if (buffer.Length < 1)
        {
            type = default;
            return false;
        }

        type = (VoiceMessageType)buffer[0];
        return type is VoiceMessageType.Handshake
            or VoiceMessageType.HandshakeAck
            or VoiceMessageType.Audio
            or VoiceMessageType.Keepalive;
    }

    public static int WriteAudio(
        Span<byte> destination,
        uint ssrc,
        ushort sequence,
        uint timestampSamples,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > AudioFormat.MaxOpusPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Opus payload length out of range.");
        }

        var total = AudioHeaderSize + payload.Length;
        if (destination.Length < total)
        {
            throw new ArgumentException("Destination too small for audio packet.", nameof(destination));
        }

        destination[0] = (byte)VoiceMessageType.Audio;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], ssrc);
        BinaryPrimitives.WriteUInt16BigEndian(destination[5..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination[7..], timestampSamples);
        BinaryPrimitives.WriteUInt16BigEndian(destination[11..], (ushort)payload.Length);
        payload.CopyTo(destination[AudioHeaderSize..]);
        return total;
    }

    public static bool TryReadAudio(
        ReadOnlySpan<byte> buffer,
        out VoiceAudioHeader header,
        out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;

        if (buffer.Length < AudioHeaderSize)
        {
            return false;
        }

        if ((VoiceMessageType)buffer[0] != VoiceMessageType.Audio)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[11..]);
        if (payloadLength is 0 or > AudioFormat.MaxOpusPayload)
        {
            return false;
        }

        if (buffer.Length < AudioHeaderSize + payloadLength)
        {
            return false;
        }

        header = new VoiceAudioHeader(
            BinaryPrimitives.ReadUInt32BigEndian(buffer[1..]),
            BinaryPrimitives.ReadUInt16BigEndian(buffer[5..]),
            BinaryPrimitives.ReadUInt32BigEndian(buffer[7..]),
            payloadLength);

        payload = buffer.Slice(AudioHeaderSize, payloadLength);
        return true;
    }

    public static int WriteHandshake(Span<byte> destination, Guid voiceToken)
    {
        if (destination.Length < HandshakeSize)
        {
            throw new ArgumentException("Destination too small for handshake.", nameof(destination));
        }

        destination[0] = (byte)VoiceMessageType.Handshake;
        if (!voiceToken.TryWriteBytes(destination[1..HandshakeSize]))
        {
            throw new ArgumentException("Could not serialise voice token.", nameof(voiceToken));
        }

        return HandshakeSize;
    }

    public static bool TryReadHandshake(ReadOnlySpan<byte> buffer, out Guid voiceToken)
    {
        voiceToken = default;
        if (buffer.Length < HandshakeSize || (VoiceMessageType)buffer[0] != VoiceMessageType.Handshake)
        {
            return false;
        }

        voiceToken = new Guid(buffer.Slice(1, 16));
        return true;
    }

    public static int WriteHandshakeAck(Span<byte> destination, uint ssrc) =>
        WriteSsrcOnly(destination, VoiceMessageType.HandshakeAck, ssrc, HandshakeAckSize);

    public static bool TryReadHandshakeAck(ReadOnlySpan<byte> buffer, out uint ssrc) =>
        TryReadSsrcOnly(buffer, VoiceMessageType.HandshakeAck, HandshakeAckSize, out ssrc);

    public static int WriteKeepalive(Span<byte> destination, uint ssrc) =>
        WriteSsrcOnly(destination, VoiceMessageType.Keepalive, ssrc, KeepaliveSize);

    public static bool TryReadKeepalive(ReadOnlySpan<byte> buffer, out uint ssrc) =>
        TryReadSsrcOnly(buffer, VoiceMessageType.Keepalive, KeepaliveSize, out ssrc);

    private static int WriteSsrcOnly(Span<byte> destination, VoiceMessageType type, uint ssrc, int size)
    {
        if (destination.Length < size)
        {
            throw new ArgumentException($"Destination too small for {type}.", nameof(destination));
        }

        destination[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], ssrc);
        return size;
    }

    private static bool TryReadSsrcOnly(
        ReadOnlySpan<byte> buffer,
        VoiceMessageType type,
        int size,
        out uint ssrc)
    {
        ssrc = 0;
        if (buffer.Length < size || (VoiceMessageType)buffer[0] != type)
        {
            return false;
        }

        ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer[1..]);
        return true;
    }
}
