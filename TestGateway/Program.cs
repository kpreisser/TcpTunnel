using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;

using TcpTunnel.Gateway;

namespace TestGateway;

class Program
{
    static void Main()
    {
        int port = 23654;

        var server = new Gateway(
            new List<(IPAddress? ip, int port, X509Certificate2? certificate)>()
            {
                (null, port, null)
            },
            new Dictionary<int, (string proxyClientPassword, string proxyServerPassword)>() {
                { 15, ( "testClientPasswort", "testServerPassword") }
            },
            Console.WriteLine);

        server.Start();

        Console.WriteLine($"Gateway started at port {port}.");
        Console.ReadLine();
        Console.WriteLine("Stopping gateway...");
        server.Stop();
        Console.WriteLine("Gateway stopped.");
    }
}
