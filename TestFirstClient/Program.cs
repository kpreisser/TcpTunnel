using System;
using System.Collections.Generic;
using System.Text;

using TcpTunnel.Client;

namespace TestFirstClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var firstClient = new TcpTunnelClient("127.0.0.1", 23654, false, 15, Encoding.UTF8.GetBytes("testPasswort"), new List<TcpTunnelConnectionDescriptor>()
            {
                //new TcpTunnelConnectionDescriptor(null, 8080, "www.preisser-it.de", 80)
                new TcpTunnelConnectionDescriptor(null, 43, "whois.ripe.net", 43)
            });

            firstClient.Start();

            Console.WriteLine($"Client started.");
            Console.ReadKey();
            Console.WriteLine("Stopping client...");
            firstClient.Stop();
            Console.WriteLine("Client stopped.");

            //new System.Threading.Thread(() =>
            //{
            //    while (true)
            //    {
            //        System.Threading.Thread.Sleep(3000);
            //        GC.Collect();
            //    }
            //}).Start();
        }
    }
}
