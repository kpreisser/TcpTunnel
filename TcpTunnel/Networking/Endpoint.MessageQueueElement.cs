using System;

namespace TcpTunnel.Networking;

internal abstract partial class Endpoint
{
    private struct MessageQueueElement
    {
        public Memory<byte>? Message;

        public bool IsTextMessage;

        public bool IsQueueEndElement;

        public bool EnterFaultedState;
    }
}
