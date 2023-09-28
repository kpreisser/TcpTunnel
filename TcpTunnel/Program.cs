using System;
using System.Threading;

using TcpTunnel.Runner;
using TcpTunnel.ServiceSupport;
using TcpTunnel.Utils;

namespace TcpTunnel
{
    class Program
    {
        static void Main(string[] args)
        {
            // Ensure to register a handler that terminates the app in case of an OOME.
            // See the method docs for more information.
            ExceptionUtils.RegisterFirstChanceOutOfMemoryExceptionHandler();

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

                // Simply wait infinitely (until the process is terminated), as we don't have
                // any form of a shutdown sequence.
                Thread.Sleep(Timeout.Infinite);
            }
        }
    }
}
