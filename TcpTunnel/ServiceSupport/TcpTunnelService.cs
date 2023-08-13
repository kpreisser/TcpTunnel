using System.Runtime.Versioning;
using System.ServiceProcess;

using TcpTunnel.Runner;

namespace TcpTunnel.ServiceSupport;

[SupportedOSPlatform("windows")]
internal class TcpTunnelService : ServiceBase
{
    public const string TcpTunnelServiceName = "TcpTunnel";

    private readonly TcpTunnelRunner runner = new();

    public TcpTunnelService()
    {
        this.InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.ServiceName = TcpTunnelServiceName;
    }

    protected override void OnStart(string[] args)
    {
        this.runner.Start();
    }

    protected override void OnStop()
    {
        this.runner.Stop();
    }

    protected override void OnShutdown()
    {
        // OnShutdown is called instead of OnStop when services are stopped due to
        // the OS shutting down.
        this.runner.Stop();
    }

    internal static void RunService()
    {
        var servicesToRun = new[] {
            new TcpTunnelService()
        };

        Run(servicesToRun);
    }
}
