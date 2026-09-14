using System.Net;
using System.Net.Sockets;

namespace Whisper.Shared.Net;

public sealed class UdpSocketTransport : IUdpTransport
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private bool _disposed;

    public IPEndPoint? LocalEndPoint => _socket.IsBound ? (IPEndPoint?)_socket.LocalEndPoint : null;

    public void Bind(IPEndPoint endPoint)
    {
        if (OperatingSystem.IsWindows())
        {
            // SIO_UDP_CONNRESET. Without this, an ICMP port-unreachable from a peer that
            // has gone away surfaces as ConnectionReset on the next receive and kills the loop.
            _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }

        _socket.Bind(endPoint);
    }

    public ValueTask SendToAsync(
        ReadOnlyMemory<byte> buffer,
        IPEndPoint destination,
        CancellationToken cancellationToken = default)
    {
        var send = _socket.SendToAsync(buffer, SocketFlags.None, destination, cancellationToken);
        return Await(send);

        static async ValueTask Await(ValueTask<int> pending) => await pending.ConfigureAwait(false);
    }

    public async ValueTask<UdpDatagram> ReceiveFromAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var result = await _socket
            .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
            .ConfigureAwait(false);

        return new UdpDatagram(result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _socket.Dispose();
    }
}
