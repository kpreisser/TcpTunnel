using System;

namespace TcpTunnel.Networking;

internal abstract partial class Endpoint
{
    #region ---------- Private types ----------

    private struct MessageQueueElement
    {
        #region ---------- Public fields ----------

        public Memory<byte>? Message;

        public bool IsTextMessage;

        public bool IsQueueEndElement;

        public bool EnterFaultedState;

        #endregion
    }

    #endregion
}
