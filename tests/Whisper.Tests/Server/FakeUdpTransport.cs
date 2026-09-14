using System.Net;
using System.Threading.Channels;
using Whisper.Shared.Net;

namespace Whisper.Tests.Server;

/// <summary>
/// Records everything the relay sends and lets a test feed datagrams in, so the relay's
/// validation and forwarding rules can be exercised with no socket involved.
/// </summary>
internal sealed class FakeUdpTransport : IUdpTransport
{
    private readonly Channel<(byte[] Payload, IPEndPoint Source)> _inbound =
        Channel.CreateUnbounded<(byte[], IPEndPoint)>();

    public IPEndPoint? LocalEndPoint { get; private set; }

    public List<(byte[] Payload, IPEndPoint Destination)> Sent { get; } = [];

    public void Bind(IPEndPoint endPoint) => LocalEndPoint = endPoint;

    public void Enqueue(byte[] payload, IPEndPoint source) => _inbound.Writer.TryWrite((payload, source));

    public ValueTask SendToAsync(
        ReadOnlyMemory<byte> buffer,
        IPEndPoint destination,
        CancellationToken cancellationToken = default)
    {
        Sent.Add((buffer.ToArray(), destination));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<UdpDatagram> ReceiveFromAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var (payload, source) = await _inbound.Reader.ReadAsync(cancellationToken);
        payload.CopyTo(buffer);
        return new UdpDatagram(payload.Length, source);
    }

    public void Dispose()
    {
    }
}
