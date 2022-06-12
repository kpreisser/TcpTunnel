using System;

using TcpTunnel.ServiceSupport;

namespace TcpTunnel
{
    class Program
    {
        static void Main(string[] args)
        {
            if (OperatingSystem.IsWindows() && Array.IndexOf(args, "-service") >= 0)
            {
                // Run the application as a service that has been installed with InstallUtil.exe
                TcpTunnelService.RunService();
            }
            else
            {
                Console.WriteLine("Starting...");
                var runner = new TcpTunnelRunner();
                runner.Start();

                Console.WriteLine("Started. Press key to exit.");
                Console.ReadKey();

                Console.WriteLine("Stopping...");
                runner.Stop();
            }
        }
    }
}
