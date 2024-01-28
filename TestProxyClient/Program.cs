using System;
using System.Collections.Generic;
using System.Text;

using TcpTunnel.Proxy;

namespace TestProxyClient;

class Program
{
    static void Main()
    {
        // Restrict the target endpoints.
        var allowedTargetEndpoints = new List<(string host, int port)>
        {
            ("www.google.com", 80),
            ("whois.ripe.net", 43)
        };

        var proxyClient = new Proxy(
            gatewayHost: "localhost",
            gatewayPort: 23654,
            gatewayUseSsl: false,
            sessionId: 15,
            sessionPasswordBytes: Encoding.UTF8.GetBytes("testClientPasswort"),
            proxyServerConnectionDescriptors: null,
            proxyClientAllowedTargetEndpoints: allowedTargetEndpoints,
            logger: Console.WriteLine);

        proxyClient.Start();

        Console.WriteLine($"Proxy-Client started.");
        Console.ReadLine();
        Console.WriteLine("Stopping Proxy-Client...");
        proxyClient.Stop();
        Console.WriteLine("Proxy-Client stopped.");
    }
}
