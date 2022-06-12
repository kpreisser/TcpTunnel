using System;
using System.Text;

namespace TcpTunnel.Networking;

internal struct ReceivedMessage
{
    #region ---------- Private fields ----------

    private readonly Memory<byte> buffer;

    private readonly ReceivedMessageType type;

    private byte[]? convertedBytes;

    private string? convertedString;

    #endregion

    #region ---------- Public constructors ----------

    public ReceivedMessage(Memory<byte> buffer, ReceivedMessageType type)
    {
        this.buffer = buffer;
        this.type = type;
        this.convertedBytes = null;
        this.convertedString = null;
    }

    #endregion

    #region ---------- Public properties ----------

    public ReceivedMessageType Type
    {
        get => this.type;
    }

    /// <summary>
    /// Returns the raw byte segment. Note (esp. for the TcpClientEndpoint) that
    /// this buffer may be reused when reading the next packet.
    /// </summary>
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

    #endregion        
}
