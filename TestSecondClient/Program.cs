using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TcpTunnel.Client;

namespace TestSecondClient
{
    class Program
    {
        static void Main(string[] args)
        {
            TcpTunnelClient firstClient = new TcpTunnelClient("localhost", 23654, false, 15, "testPasswort", null);
            firstClient.Start();
            Console.WriteLine($"Client started.");
            Console.ReadKey();
            Console.WriteLine("Stopping client...");
            firstClient.Stop();
            Console.WriteLine("Client stopped.");
        }
    }
}
