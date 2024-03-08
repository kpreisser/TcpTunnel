using System;

namespace TcpTunnel.Networking;

internal struct ReceivedMessage
{
    private readonly Memory<byte> buffer;

    private readonly ReceivedMessageType type;

    public ReceivedMessage(Memory<byte> buffer, ReceivedMessageType type)
    {
        this.buffer = buffer;
        this.type = type;
    }

    public ReceivedMessageType Type
    {
        get => this.type;
    }

    /// <summary>
    /// Returns the raw byte memory.
    /// </summary>
    /// <remarks>
    /// Note (esp. for the <see cref="TcpConnection"/>) that this buffer may
    /// be reused when reading the next packet.
    /// </remarks>
    public Memory<byte> Buffer
    {
        get => this.buffer;
    }
}
