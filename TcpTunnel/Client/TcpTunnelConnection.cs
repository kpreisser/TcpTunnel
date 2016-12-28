using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TcpTunnel.Client
{
    internal class TcpTunnelConnection
    {
        private readonly TcpClient remoteClient;
        private ConcurrentQueue<PacketQueueEntry> transmitPacketQueue;


        private class PacketQueueEntry
        {
            // If null, close the connection
            public byte[] packet;
        }
    }
}
