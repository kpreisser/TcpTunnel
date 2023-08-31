using System;

namespace TcpTunnel.Networking;

internal abstract partial class Connection
{
    private struct MessageQueueElement
    {
        public Memory<byte>? Message;

        public bool IsTextMessage;

        public bool ExitSendTask;
    }
}
