using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

using TcpTunnel.Proxy;
using TcpTunnel.Utils;

namespace TcpTunnel.Runner
{
    internal class TcpTunnelRunner
    {
        private static readonly XNamespace xmlNamespace = "https://github.com/kpreisser/TcpTunnel";

        private readonly Action<string>? logger;

        private readonly List<IInstance> instances;

        public TcpTunnelRunner(Action<string>? logger = null)
        {
            this.logger = logger;

            this.instances = new();
        }

        public void Start()
        {
            // Load the settings text file.
            XDocument settingsDoc;
            string settingsPath = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath)!,
                "settings.xml");

            using (var fileStream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
                settingsDoc = XDocument.Load(fileStream, LoadOptions.None);

            var rootEl = settingsDoc.Root;
            if (rootEl?.Name.LocalName is not "Settings" || rootEl.Name.Namespace != xmlNamespace)
                throw new InvalidDataException(
                    $"Root element 'Settings' not found in namespace '{xmlNamespace}'.");

            var instanceElements = rootEl.Elements(xmlNamespace + "Instance");

            try
            {
                foreach (var instanceElement in instanceElements)
                {
                    string applicationType = instanceElement.Attribute("type")?.Value ??
                        throw new InvalidDataException("Missing attribute 'type'.");

                    bool isProxyListener = false;

                    if (string.Equals(applicationType, "gateway", StringComparison.OrdinalIgnoreCase))
                    {
                        var listenerEntries = new List<(IPAddress? address, int port, X509Certificate2? certificate)>();

                        foreach (var listenerElement in instanceElement.Elements(xmlNamespace + "Listener"))
                        {
                            var ip = listenerElement.Attribute("ip")?.Value is { } ipString ?
                                IPAddress.Parse(ipString) :
                                null;

                            ushort port = ushort.Parse(
                                listenerElement.Attribute("port")?.Value ??
                                    throw new InvalidDataException("Missing attribute 'port'."),
                                CultureInfo.InvariantCulture);

                            // Check if we need to use SSL/TLS.
                            string? certificateThumbprint = listenerElement.Attribute("certificateHash")?.Value;

                            string? certificatePfxFilePath = listenerElement.Attribute("certificatePfxFilePath")?.Value;
                            string? certificatePfxPassword = listenerElement.Attribute("certificatePfxPassword")?.Value;

                            string? certificatePemFilePath = listenerElement.Attribute("certificatePemFilePath")?.Value;
                            string? certificatePemKeyFilePath = listenerElement.Attribute("certificatePemKeyFilePath")?.Value;

                            var certificate = default(X509Certificate2);

                            if (certificateThumbprint is not null)
                            {
                                if (!OperatingSystem.IsWindows())
                                    throw new PlatformNotSupportedException(
                                        "Getting a certificate from the Windows Certificate Store is only " +
                                        "supported on Windows.");

                                // Get the certificate
                                certificate = CertificateUtils.GetCurrentUserOrLocalMachineCertificateFromFingerprint(
                                    certificateThumbprint);
                            }
                            else if (certificatePfxFilePath is not null)
                            {
                                // Load the certificate from a PFX file.
                                certificate = new X509Certificate2(certificatePfxFilePath, certificatePfxPassword);

                                if (!certificate.HasPrivateKey)
                                    throw new Exception("Certificate doesn't have a private key.");
                            }
                            else if (certificatePemFilePath is not null)
                            {
                                if (certificatePemKeyFilePath is null)
                                    throw new ArgumentException("The 'certificatePemKeyFilePath' attribute needs to be specified.");

                                certificate = X509Certificate2.CreateFromPemFile(certificatePemFilePath, certificatePemKeyFilePath);

                                if (!certificate.HasPrivateKey)
                                    throw new Exception("Certificate doesn't have a private key.");
                            }

                            listenerEntries.Add((ip, port, certificate));
                        }

                        var sessions = new Dictionary<int, (
                            string proxyClientPassword,
                            string proxyServerPassword
                        )>();

                        foreach (var sessionElement in instanceElement.Elements(xmlNamespace + "Session"))
                        {
                            int sessionId = int.Parse(
                                sessionElement.Attribute("id")?.Value ??
                                    throw new InvalidDataException("Missing attribute 'id'."),
                                CultureInfo.InvariantCulture);

                            string proxyClientPassword = sessionElement.Attribute("proxyClientPassword")?.Value ??
                                    throw new InvalidDataException("Missing attribute 'proxyClientPassword'.");

                            string proxyServerPassword = sessionElement.Attribute("proxyServerPassword")?.Value ??
                                    throw new InvalidDataException("Missing attribute 'proxyServerPassword'.");

                            if (!sessions.TryAdd(sessionId, (proxyClientPassword, proxyServerPassword)))
                                throw new InvalidDataException(
                                    $"Duplicate session ID \"{sessionId.ToString(CultureInfo.InvariantCulture)}\".");
                        }

                        var server = new Gateway.Gateway(listenerEntries, sessions, this.logger);
                        server.Start();

                        this.instances.Add(server);
                    }
                    else if (string.Equals(applicationType, "proxy-client", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(applicationType, "proxy-server", StringComparison.OrdinalIgnoreCase) &&
                            (isProxyListener = true))
                    {
                        string host = instanceElement.Attribute("host")?.Value ??
                               throw new InvalidDataException("Missing attribute 'host'.");

                        ushort port = ushort.Parse(
                           instanceElement.Attribute("port")?.Value ??
                               throw new InvalidDataException("Missing attribute 'port'."),
                           CultureInfo.InvariantCulture);

                        bool useSsl = instanceElement.Attribute("useSsl")?.Value is string useSslParam &&
                            (useSslParam is "1" ||
                                string.Equals(
                                    useSslParam,
                                    "true",
                                    StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(
                                    useSslParam,
                                    "yes",
                                    StringComparison.OrdinalIgnoreCase));

                        var sessionElement = instanceElement.Element(xmlNamespace + "Session") ??
                            throw new InvalidDataException("Missing 'Session' element.");

                        int sessionId = int.Parse(
                            sessionElement.Attribute("id")?.Value ??
                                throw new InvalidDataException("Missing attribute 'id'."),
                            CultureInfo.InvariantCulture);

                        string password = sessionElement.Attribute("password")?.Value ??
                            throw new InvalidDataException("Missing attribute 'password'.");

                        var descriptors = default(List<ProxyServerConnectionDescriptor>);
                        var allowedTargetEndpoints = default(List<(string host, int port)>);

                        if (isProxyListener)
                        {
                            descriptors = new List<ProxyServerConnectionDescriptor>();

                            foreach (var bindingElement in sessionElement.Elements(xmlNamespace + "Binding"))
                            {
                                var listenIp = bindingElement.Attribute("listenIp")?.Value is { } listenIpString ?
                                    IPAddress.Parse(listenIpString) :
                                    null;

                                ushort listenPort = ushort.Parse(
                                   bindingElement.Attribute("listenPort")?.Value ??
                                       throw new InvalidDataException("Missing attribute 'listenPort'."),
                                   CultureInfo.InvariantCulture);

                                string targetHost = bindingElement.Attribute("targetHost")?.Value ??
                                    throw new InvalidDataException("Missing attribute 'targetHost'.");

                                ushort targetPort = ushort.Parse(
                                   bindingElement.Attribute("targetPort")?.Value ??
                                       throw new InvalidDataException("Missing attribute 'targetPort'."),
                                   CultureInfo.InvariantCulture);

                                descriptors.Add(new ProxyServerConnectionDescriptor(
                                    listenIp,
                                    listenPort,
                                    targetHost,
                                    targetPort));
                            }
                        }
                        else
                        {
                            var allowedTargetEndpointsElement = instanceElement.Element(xmlNamespace + "AllowedTargetEndpoints");

                            if (allowedTargetEndpointsElement is not null)
                            {
                                allowedTargetEndpoints = new List<(string host, int port)>();

                                foreach (var endpointElement in allowedTargetEndpointsElement.Elements(xmlNamespace + "Endpoint"))
                                {
                                    string endpointHost = endpointElement.Attribute("host")?.Value ??
                                        throw new InvalidDataException("Missing attribute 'host'.");
                                    string endpointPort = endpointElement.Attribute("port")?.Value ??
                                        throw new InvalidDataException("Missing attribute 'port'.");

                                    allowedTargetEndpoints.Add((endpointHost, int.Parse(endpointPort, CultureInfo.InvariantCulture)));
                                }
                            }
                        }

                        var client = new Proxy.Proxy(
                            host,
                            port,
                            useSsl,
                            sessionId,
                            Encoding.UTF8.GetBytes(password),
                            descriptors,
                            allowedTargetEndpoints,
                            this.logger);

                        client.Start();

                        this.instances.Add(client);
                    }
                    else
                    {
                        throw new InvalidDataException("Unknown application type.");
                    }
                }
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                this.Stop();
                throw;
            }
        }

        public void Stop()
        {
            for (int i = this.instances.Count - 1; i >= 0; i--)
                instances[i].Stop();

            this.instances.Clear();
        }
    }
}
