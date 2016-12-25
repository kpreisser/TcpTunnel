using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TcpTunnel.Server
{
    internal class Session
    {
        public byte[] SessionID { get; }

        public List<ConnectionHandler> ActiveConnections { get; }

        public Session(byte[] sessionID)
        {
            this.SessionID = sessionID;
        }
    }
}
