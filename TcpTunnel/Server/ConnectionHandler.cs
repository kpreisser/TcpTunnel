using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TcpTunnel.SocketInterfaces;

namespace TcpTunnel.Server
{
    /*
     * PROTOCOL (Frame Payload):
     * 0x00: Server Communication:
     *    -> 0x00: (Client to Server) Authentication + Login-Prerequisite-String + Session-Key
     *    -> 0x01: (Server to Client) Authentication failed, try again.
     *    -> 0x02: (Server to Client) Session Status (sent after successfull authentication and during runtime if status changes):
     *           -> 0x00: Remote client is available.
     *           -> 0x01: Remote client is unavailable.
     * 0x01: Remove client communication. The server forwards the packet to the remote client if available.
     */
    internal class ConnectionHandler
    {
        private readonly TcpTunnelServer server;
        private readonly TcpClientEndpoint connection;

        public ConnectionHandler(TcpTunnelServer server, TcpClientEndpoint connection)
        {
            this.server = server;
            this.connection = connection;

        }

        public async Task RunAsync()
        {

        }
    }
}
