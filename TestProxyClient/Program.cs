using System;
using System.Text;

using TcpTunnel.Proxy;

namespace TestProxyClient
{
    class Program
    {
        static void Main()
        {
            static void LogConsole(string s) => Console.WriteLine(s);

            var firstClient = new Proxy("localhost", 23654, false, 15, Encoding.UTF8.GetBytes("testClientPasswort"), null, LogConsole);
            firstClient.Start();

            Console.WriteLine($"Proxy-Client started.");
            Console.ReadLine();
            Console.WriteLine("Stopping Proxy-Client...");
            firstClient.Stop();
            Console.WriteLine("Proxy-Client stopped.");

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
