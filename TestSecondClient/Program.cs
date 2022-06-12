using System;
using System.Text;

using TcpTunnel.Client;

namespace TestSecondClient
{
    class Program
    {
        static void Main()
        {
            static void LogConsole(string s) => Console.WriteLine(s);

            var firstClient = new TcpTunnelClient("localhost", 23654, false, 15, Encoding.UTF8.GetBytes("testPasswort"), null, LogConsole);
            firstClient.Start();

            Console.WriteLine($"Client started.");
            Console.ReadLine();
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
