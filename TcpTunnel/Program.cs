using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TcpTunnel.Server;

namespace TcpTunnel
{
    class Program
    {
        static void Main(string[] args)
        {
#if DEBUG
            int port = 23654;   
            var server = new TcpTunnelServer(port, null, new Dictionary<int, string>() { { 1, "testPasswort" } });
            server.Start();
            Console.WriteLine($"Server started at port {port}. Press key to stop.");
            Console.ReadKey();
            Console.WriteLine("Stopping server...");
            server.Stop();
            Console.WriteLine("Server stopped.");
#endif
        }
    }
}
