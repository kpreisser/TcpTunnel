using System;
using System.Collections.Generic;

using TcpTunnel.Gateway;

namespace TestGateway
{
    class Program
    {
        static void Main()
        {
            static void LogConsole(string s) => Console.WriteLine(s);

            int port = 23654;
            var server = new Gateway(port, null, new Dictionary<int, string>() { { 15, "testPasswort" } }, LogConsole);
            server.Start();

            Console.WriteLine($"Gateway started at port {port}.");
            Console.ReadLine();
            Console.WriteLine("Stopping gateway...");
            server.Stop();
            Console.WriteLine("Gateway stopped.");

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
