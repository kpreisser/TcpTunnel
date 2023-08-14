using System;
using System.Threading;

using TcpTunnel.Runner;
using TcpTunnel.ServiceSupport;

namespace TcpTunnel
{
    class Program
    {
        static void Main(string[] args)
        {
            if (OperatingSystem.IsWindows() && Array.IndexOf(args, "-service") >= 0)
            {
                // Run the application as a service.
                TcpTunnelService.RunService();
            }
            else
            {
                static void LogConsole(string s) => Console.WriteLine(s);

                Console.WriteLine("Starting...");
                var runner = new TcpTunnelRunner(LogConsole);
                runner.Start();

                Console.WriteLine("Started. Press Ctrl+C or send a SIGTERM to exit.");

                // Simply wait infinitely (until the process is terminated), as we have any form
                // of a shutdown sequence.
                Thread.Sleep(Timeout.Infinite);
            }
        }
    }
}
