using System;
using System.Collections.Generic;
using System.Text;

using TcpTunnel.Proxy;

namespace TestProxyClient
{
    class Program
    {
        static void Main()
        {
            static void LogConsole(string s) => Console.WriteLine(s);

            // Restrict the target endpoints.
            var allowedTargetEndpoints = new List<(string host, int port)>
            {
                ("www.google.com", 80),
                ("whois.ripe.net", 43)
            };

            var firstClient = new Proxy(
                gatewayHost: "localhost",
                gatewayPort: 23654,
                gatewayUseSsl: false,
                sessionId: 15,
                sessionPasswordBytes: Encoding.UTF8.GetBytes("testClientPasswort"),
                proxyServerConnectionDescriptors: null,
                proxyClientAllowedTargetEndpoints: allowedTargetEndpoints,
                logger: LogConsole);

            firstClient.Start();

            Console.WriteLine($"Proxy-Client started.");
            Console.ReadLine();
            Console.WriteLine("Stopping Proxy-Client...");
            firstClient.Stop();
            Console.WriteLine("Proxy-Client stopped.");
        }
    }
}
