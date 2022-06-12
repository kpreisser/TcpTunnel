namespace TcpTunnel.Networking;

internal enum ReceivedMessageType : int
{
    /// <summary>
    /// Specifies that the message type is unknown and you should treat it as part of
    /// a stream (as no framing was applied).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A framed byte message was received.
    /// </summary>
    ByteMessage = 1,

    /// <summary>
    /// A framed string message was received.
    /// </summary>
    StringMessage = 2
}
