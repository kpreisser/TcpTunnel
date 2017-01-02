using System;
using System.Net;

namespace TcpTunnel.Client
{
    public class TcpTunnelConnectionDescriptor
    {
        public IPAddress ListenIP { get; } // can be null
        public int ListenPort { get; }
        public string RemoteHost { get; }
        public int RemotePort { get; }

        public TcpTunnelConnectionDescriptor(IPAddress listenIP, int listenPort, string remoteHost, int remotePort)
        {
            if (remoteHost == null)
                throw new ArgumentNullException(nameof(remoteHost));

            this.ListenIP = listenIP;
            this.ListenPort = listenPort;
            this.RemoteHost = remoteHost;
            this.RemotePort = remotePort;
        }
    }
}
