using System;
using System.Collections.Generic;
using System.Text;

using TcpTunnel.Proxy;

namespace TestProxyServer
{
    class Program
    {
        static void Main()
        {
            static void LogConsole(string s) => Console.WriteLine(s);

            var firstClient = new Proxy("127.0.0.1", 23654, false, 15, Encoding.UTF8.GetBytes("testServerPassword"), new List<ProxyServerConnectionDescriptor>()
            {
                new ProxyServerConnectionDescriptor(null, 80, "www.google.com", 80),
                new ProxyServerConnectionDescriptor(null, 43, "whois.ripe.net", 43)
            }, LogConsole);

            firstClient.Start();

            Console.WriteLine($"Proxy-Server started.");
            Console.ReadLine();
            Console.WriteLine("Stopping Proxy-Server...");
            firstClient.Stop();
            Console.WriteLine("Proxy-Server stopped.");
        }
    }
}
