using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TcpTunnel.Server
{
    internal class Session
    {
        public string Password { get; }

        public long CurrentIteration { get; private set; }

        public ConnectionHandler[] Clients { get; } = new ConnectionHandler[2];


        public Session(string password)
        {
            this.Password = password;
        }

        public void UpdateIteration()
        {
            if (this.Clients[0] != null && this.Clients[1] != null)
            {
                unchecked
                {
                    this.CurrentIteration++;
                }
            }
        }
    }
}
