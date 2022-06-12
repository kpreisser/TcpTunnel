using System;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace TcpTunnel.ServiceSupport;

[SupportedOSPlatform("windows")]
internal class TcpTunnelService : ServiceBase
{
    private readonly TcpTunnelRunner runner = new TcpTunnelRunner();

    public TcpTunnelService()
    {
        this.InitializeComponent();
    }

    //private EventLog eventLog1;

    public const string MyServiceName = "TcpTunnelService";

    private void InitializeComponent()
    {
        //this.eventLog1 = new EventLog();
        //((System.ComponentModel.ISupportInitialize)(this.eventLog1)).BeginInit();

        // 
        // Service1
        // 
        this.ServiceName = MyServiceName;

        //((System.ComponentModel.ISupportInitialize)(this.eventLog1)).EndInit();

        //string eventLogSource = MyServiceName;

        //if (!EventLog.SourceExists(eventLogSource))
        //{
        //    EventLog.CreateEventSource(eventLogSource, string.Empty);
        //}

        //eventLog1.Source = eventLogSource;
        //eventLog1.Log = string.Empty;
    }

    protected override void OnStart(string[] args)
    {
        this.runner.Start();

        this.LogInfo("Service started.");
    }

    protected override void OnStop()
    {
        this.runner.Stop();

        this.LogInfo("Service stopped.");
    }

    private void LogInfo(string text)
    {
        //eventLog1.WriteEntry(text, EventLogEntryType.Information);
    }

    private void LogWarning(string text)
    {
        //eventLog1.WriteEntry(text, EventLogEntryType.Warning);
    }

    private void LogException(Exception ex)
    {
        //eventLog1.WriteEntry("Exception: " + ex.ToString(), EventLogEntryType.Error);
    }
    
    internal static void RunService()
    {
        var servicesToRun = new[] {
            new TcpTunnelService()
        };

        ServiceBase.Run(servicesToRun);
    }
}
