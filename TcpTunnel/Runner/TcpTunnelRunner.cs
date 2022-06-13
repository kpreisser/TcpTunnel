using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using TcpTunnel.Proxy;
using TcpTunnel.Gateway;
using TcpTunnel.Utils;

namespace TcpTunnel.Runner
{
    internal class TcpTunnelRunner
    {
        private readonly Action<string>? logger;

        private Gateway.Gateway? server;
        private Proxy.Proxy? client;

        public TcpTunnelRunner(Action<string>? logger = null)
        {
            this.logger = logger;
        }

        public void Start()
        {
            // Load the settings text file.
            string settingsPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "settings.txt");
            string settingsContent = File.ReadAllText(settingsPath, Encoding.UTF8).Replace("\r", "");
            string[] settingsLines = settingsContent.Split("\n", StringSplitOptions.None);

            // Remove "#"
            for (int i = 0; i < settingsLines.Length; i++)
            {
                int commentIdx = settingsLines[i].IndexOf("#");
                if (commentIdx >= 0)
                    settingsLines[i] = settingsLines[i][..commentIdx];
            }

            string applicationType = settingsLines[0].Trim();
            bool isProxyListener = false;

            if (string.Equals(applicationType, "gateway", StringComparison.OrdinalIgnoreCase))
            {
                // Line 2: Port, Line 3: Certificate Thumbprint (if not empty), Lines 4...: SessionID + "," + SessionPasswort
                int port = int.Parse(settingsLines[1].Trim(), CultureInfo.InvariantCulture);
                string certificateThumbprint = settingsLines[2].Trim();
                var certificate = default(X509Certificate2);

                if (certificateThumbprint.Length > 0)
                {
                    certificate = CertificateUtils.GetCurrentUserOrLocalMachineCertificateFromFingerprint(
                        certificateThumbprint);
                }

                var sessions = new Dictionary<int, string>();
                for (int i = 3; i < settingsLines.Length; i++)
                {
                    string line = settingsLines[i];

                    int commaIndex = line.IndexOf(",");
                    if (commaIndex > 0)
                    {
                        int sessionId = int.Parse(line[..commaIndex], CultureInfo.InvariantCulture);
                        string sessionPassword = line[(commaIndex + 1)..];
                        sessions.Add(sessionId, sessionPassword);
                    }
                }

                this.server = new Gateway.Gateway(port, certificate, sessions, this.logger);
                this.server.Start();
            }
            else if (string.Equals(applicationType, "proxy-client", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(applicationType, "proxy-server", StringComparison.OrdinalIgnoreCase) &&
                    (isProxyListener = true))
            {
                // Hostname, Port, Usessl, SessionID, SessionPasswort, Line 7....: Port + "," + Hostname
                string hostname = settingsLines[1].Trim();
                int port = int.Parse(settingsLines[2].Trim(), CultureInfo.InvariantCulture);
                bool useSsl = settingsLines[3] is string line3 && (line3 is "1" || string.Equals(
                    line3,
                    "true",
                    StringComparison.OrdinalIgnoreCase));

                int sessionId = int.Parse(settingsLines[4].Trim(), CultureInfo.InvariantCulture);
                string sessionPassword = settingsLines[5];

                var descriptors = default(List<ProxyServerConnectionDescriptor>);

                if (isProxyListener)
                {
                    descriptors = new List<ProxyServerConnectionDescriptor>();

                    for (int i = 6; i < settingsLines.Length; i++)
                    {
                        string line = settingsLines[i].Trim();
                        var lineEntries = line.Split(new string[] { "," }, StringSplitOptions.None);

                        var listenIP = lineEntries[0].Length is 0 ? null : IPAddress.Parse(lineEntries[0]);
                        int listenPort = int.Parse(lineEntries[1], CultureInfo.InvariantCulture);
                        string remoteHost = lineEntries[2];
                        int remotePort = int.Parse(lineEntries[3], CultureInfo.InvariantCulture);

                        descriptors.Add(new ProxyServerConnectionDescriptor(
                            listenIP,
                            listenPort,
                            remoteHost,
                            remotePort));

                    }
                }

                this.client = new Proxy.Proxy(
                    hostname,
                    port,
                    useSsl,
                    sessionId,
                    Encoding.UTF8.GetBytes(sessionPassword),
                    descriptors,
                    this.logger);

                this.client.Start();
            }
            else
            {
                throw new ArgumentException("Unknown application type.");
            }
        }

        public void Stop()
        {
            this.server?.Stop();
            this.client?.Stop();
        }
    }
}
