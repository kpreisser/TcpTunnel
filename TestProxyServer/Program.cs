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

            var connectionDescriptors = new List<ProxyServerConnectionDescriptor>()
            {
                new ProxyServerConnectionDescriptor(null, 80, "www.google.com", 80),
                new ProxyServerConnectionDescriptor(null, 43, "whois.ripe.net", 43)
            };

            var firstClient = new Proxy(
                gatewayHost: "127.0.0.1",
                gatewayPort: 23654,
                gatewayUseSsl: false,
                sessionId: 15,
                sessionPasswordBytes: Encoding.UTF8.GetBytes("testServerPassword"),
                proxyServerConnectionDescriptors: connectionDescriptors,
                proxyClientAllowedTargetEndpoints: null,
                logger: LogConsole);

            firstClient.Start();

            Console.WriteLine($"Proxy-Server started.");
            Console.ReadLine();
            Console.WriteLine("Stopping Proxy-Server...");
            firstClient.Stop();
            Console.WriteLine("Proxy-Server stopped.");
        }
    }
}
