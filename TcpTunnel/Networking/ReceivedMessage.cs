using System;
using System.Text;

namespace TcpTunnel.Networking;

internal struct ReceivedMessage
{
    private readonly Memory<byte> buffer;

    private readonly ReceivedMessageType type;

    private byte[]? convertedBytes;

    private string? convertedString;

    public ReceivedMessage(Memory<byte> buffer, ReceivedMessageType type)
    {
        this.buffer = buffer;
        this.type = type;
        this.convertedBytes = null;
        this.convertedString = null;
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

    public byte[] ByteMessage
    {
        get => this.convertedBytes ??= this.buffer.ToArray();
    }

    public string StringMessage
    {
        get => this.convertedString ??= Encoding.UTF8.GetString(this.buffer.Span);
    }
}
