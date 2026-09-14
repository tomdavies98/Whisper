using System.Net;

namespace Whisper.Shared.Net;

public readonly record struct UdpDatagram(int BytesReceived, IPEndPoint RemoteEndPoint);

/// <summary>
/// The seam that keeps sockets out of the relay and voice-client logic, so routing
/// and handshake behaviour can be exercised without binding a real port.
/// </summary>
public interface IUdpTransport : IDisposable
{
    IPEndPoint? LocalEndPoint { get; }

    void Bind(IPEndPoint endPoint);

    ValueTask SendToAsync(ReadOnlyMemory<byte> buffer, IPEndPoint destination, CancellationToken cancellationToken = default);

    ValueTask<UdpDatagram> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
