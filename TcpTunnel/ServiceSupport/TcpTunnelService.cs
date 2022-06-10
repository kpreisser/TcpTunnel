using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace TcpTunnel.ServiceSupport
{
    internal class TcpTunnelService : ServiceBase
    {
        private TcpTunnelRunner runner = new TcpTunnelRunner();

        public TcpTunnelService()
        {
            InitializeComponent();
        }


        #region ServiceDetails


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


        #endregion


        protected override void OnStart(string[] args)
        {
            runner.Start();

            LogInfo("Service started.");
        }

        protected override void OnStop()
        {
            runner.Stop();

            LogInfo("Service stopped.");
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
            ServiceBase[] servicesToRun = {
                new TcpTunnelService()
            };
            ServiceBase.Run(servicesToRun);
        }
    }
}
