using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace TcpTunnel.ServiceSupport
{
    /// <summary>
    /// Installer component which installs the TcpTunnelService. Use InstallUtil.exe to install the service.
    /// This installer appends a "-service" to the image path so that the executable knows if it should run as a service.
    /// </summary>
    [RunInstaller(true)]
    public class ProjectInstaller : Installer
    {

        private ServiceProcessInstaller serviceProcessInstaller1;
        private ServiceInstaller serviceInstaller1;

        public ProjectInstaller()
        {
            InitializeComponent();

            serviceInstaller1.ServiceName = TcpTunnelService.MyServiceName;
        }

        private void InitializeComponent()
        {
            this.serviceProcessInstaller1 = new ServiceProcessInstaller();

            this.serviceInstaller1 = new ServiceInstaller();
            // 
            // serviceProcessInstaller1
            // 
            this.serviceProcessInstaller1.Account = ServiceAccount.LocalSystem;
            this.serviceProcessInstaller1.Password = null;
            this.serviceProcessInstaller1.Username = null;
            // 
            // serviceInstaller1
            // 
            this.serviceInstaller1.StartType = ServiceStartMode.Automatic;
            // 
            // ProjectInstaller
            // 
            this.Installers.AddRange(new Installer[] {
                this.serviceProcessInstaller1,
                this.serviceInstaller1
            });

        }

        private string AppendPathParameter(string path, string parameter)
        {
            if (path.Length > 0 && path[0] != '"')
            {
                path = "\"" + path + "\"";
            }
            path += " " + parameter;
            return path;
        }

        protected override void OnBeforeInstall(System.Collections.IDictionary savedState)
        {
            // Append "-service" parameter to the ImagePath
            Context.Parameters["assemblypath"] = AppendPathParameter(Context.Parameters["assemblypath"], "-service");
            base.OnBeforeInstall(savedState);
        }

        protected override void OnBeforeUninstall(System.Collections.IDictionary savedState)
        {
            // Append "-service" parameter to the ImagePath
            Context.Parameters["assemblypath"] = AppendPathParameter(Context.Parameters["assemblypath"], "-service");
            base.OnBeforeUninstall(savedState);
        }

    }

}
