using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using TcpTunnel.Client;
using TcpTunnel.Server;
using TcpTunnel.Utils;

namespace TcpTunnel
{
    internal class TcpTunnelRunner
    {
        private TcpTunnelServer server;
        private TcpTunnelClient client;

        public void Start()
        {
            // Load the settings text file.
            string settingsPath = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "settings.txt");
            string settingsContent = File.ReadAllText(settingsPath, Encoding.UTF8).Replace("\r", "");
            string[] settingsLines = settingsContent.Split(new string[] { "\n" }, StringSplitOptions.None);

            // Remove "#"
            for (int i = 0; i < settingsLines.Length; i++)
            {
                var commentIdx = settingsLines[i].IndexOf("#");
                if (commentIdx >= 0)
                    settingsLines[i] = settingsLines[i].Substring(0, commentIdx);
            }

            if (settingsLines[0] == "server")
            {
                // Line 2: Port, Line 3: Certificate Thumbprint (if not empty), Lines 4...: SessionID + "," + SessionPasswort
                int port = int.Parse(settingsLines[1], CultureInfo.InvariantCulture);
                string certificateThumbprint = settingsLines[2];
                X509Certificate2 certificate = null;
                if (certificateThumbprint.Length > 0)
                {
                    certificate = CertificateUtils.GetCurrentUserOrLocalMachineCertificateFromFingerprint(certificateThumbprint);
                    if (certificate == null)
                        throw new Exception($"Could not find certificate with thumbprint '{certificateThumbprint}'.");
                }

                IDictionary<int, string> sessions = new SortedDictionary<int, string>();
                for (int i  = 3; i < settingsLines.Length; i++)
                {
                    string line = settingsLines[i];
                    int commaIndex = line.IndexOf(",");
                    if (commaIndex > 0)
                    {
                        int sessionID = int.Parse(line.Substring(0, commaIndex), CultureInfo.InvariantCulture);
                        string sessionPassword = line.Substring(commaIndex + 1);
                        sessions.Add(sessionID, sessionPassword);
                    }
                }

                this.server = new TcpTunnelServer(port, certificate, sessions);
                this.server.Start();
            }
            else if (settingsLines[0] == "client")
            {
                // Hostname, Port, Usessl, SessionID, SessionPasswort, Line 7....: Port + "," + Hostname
                string hostname = settingsLines[1];
                int port = int.Parse(settingsLines[2], CultureInfo.InvariantCulture);
                bool usessl = settingsLines[3] == "1" || settingsLines[3].ToLowerInvariant() == "true";
                int sessionID = int.Parse(settingsLines[4], CultureInfo.InvariantCulture);
                string sessionPassword = settingsLines[5];
                IDictionary<int, string> hostPorts = new SortedDictionary<int, string>();
                for (int i = 6; i < settingsLines.Length; i++)
                {
                    string line = settingsLines[i];
                    int commaIndex = line.IndexOf(",");
                    if (commaIndex > 0)
                    {
                        int connectPort = int.Parse(line.Substring(0, commaIndex), CultureInfo.InvariantCulture);
                        string connectHostname = line.Substring(commaIndex + 1);
                        hostPorts.Add(connectPort, connectHostname);
                    }
                }

                this.client = new TcpTunnelClient(hostname, port, usessl, sessionID, sessionPassword, hostPorts.Count == 0 ? null : hostPorts);
                this.client.Start();
            }
            else
            {
                throw new InvalidDataException();
            }

        }

        public void Stop()
        {
            this.server?.Stop();
            this.client?.Stop();
        }
    }
}
