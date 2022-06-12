using System;
using System.Collections.Generic;

using TcpTunnel.Server;

namespace TestServer
{
    class Program
    {
        static void Main()
        {
            static void LogConsole(string s) => Console.WriteLine(s);

            int port = 23654;
            var server = new TcpTunnelServer(port, null, new Dictionary<int, string>() { { 15, "testPasswort" } }, LogConsole);
            server.Start();

            Console.WriteLine($"Server started at port {port}.");
            Console.ReadLine();
            Console.WriteLine("Stopping server...");
            server.Stop();
            Console.WriteLine("Server stopped.");

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
