namespace TcpTunnel.Proxy;

public partial class Proxy
{
    private struct TunnelConnectionData
    {
        /// <summary>
        /// Specifies whether the <see cref="Proxy"/> doesn't need to send an abort message
        /// when the <c>connectionFinishedHandler</c> is invoked.
        /// </summary>
        public volatile bool SuppressSendAbortMessage;
    }
}
