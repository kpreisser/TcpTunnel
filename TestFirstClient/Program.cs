using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TcpTunnel.Client;

namespace TestFirstClient
{
    class Program
    {
        static void Main(string[] args)
        {
            TcpTunnelClient firstClient = new TcpTunnelClient("127.0.0.1", 39617, true, 15, "testPasswort", new List<TcpTunnelConnectionDescriptor>()
            {
                new TcpTunnelConnectionDescriptor(null, 8080, "www.preisser-it.de", 80)
            });
            firstClient.Start();
            Console.WriteLine($"Client started.");
            Console.ReadKey();
            Console.WriteLine("Stopping client...");
            firstClient.Stop();
            Console.WriteLine("Client stopped.");
        }
    }
}
