using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.SocketInterfaces;
using TcpTunnel.Utils;

namespace TcpTunnel.Client
{
    internal class TcpTunnelClient
    {
        public const int MaxReceivePacketSize = 2 * 1024 * 1024;
        public const int MaxSendBufferSize = 5 * 1024 * 1024;

        private readonly string hostname;
        private readonly int port;
        private readonly bool useSsl;
        private readonly int sessionID;
        private readonly string sessionPassword;
        /// <summary>
        /// The first client is the one that provides server functionality by listening on specific ports.
        /// </summary>
        private readonly IDictionary<int, string> firstClientPortsAndRemoteHostnames;

        private Task readTask;
        private TcpClient tcpClient;
        private TcpClientFramingEndpoint endpoint;
        private bool stopped;

        public object SyncRoot { get; } = new object();
        
        private bool remoteClientAvailable;
        private FirstClientServer currentServer;

        public TcpTunnelClient(string hostname, int port, bool useSsl, int sessionID, string sessionPassword,
            IDictionary<int, string> firstClientPortsAndRemoteHostnames)
        {
            this.hostname = hostname;
            this.port = port;
            this.useSsl = useSsl;
            this.sessionID = sessionID;
            this.sessionPassword = sessionPassword;
            this.firstClientPortsAndRemoteHostnames = firstClientPortsAndRemoteHostnames;
        }

        public void Start()
        {
            this.readTask = Task.Run(async () => await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(RunReadTaskAsync));

        }

        public void Stop()
        {
            lock (this.SyncRoot)
            {
                stopped = true;
                if (this.tcpClient != null)
                {
                    lock (this.tcpClient) {
                        this.tcpClient.Client.Close(0);
                        this.tcpClient.Close();   
                    }
                }
            }
            
        }

        private async Task RunReadTaskAsync()
        {
            // Try to connect using IPv6. If that fails, connect using IPv4.
            while (true)
            {
                try
                {
                    for (int i = 0; i < 2; i++)
                    {
                        bool useIpv6 = i == 0;
                        var client = new TcpClient(useIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);

                        lock (this.SyncRoot)
                        {
                            if (this.stopped)
                                return;
                            this.tcpClient = client;
                        }

                        try
                        {
                            await this.tcpClient.ConnectAsync(hostname, port);
                            break;
                        }
                        catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                        {
                            Debug.WriteLine(ex.ToString());
                            lock (this.tcpClient)
                                this.tcpClient.Dispose();
                            if (i == 1)
                                throw;
                        }
                    }

                    var endpoint = new TcpClientFramingEndpoint(this.tcpClient, true, false, ModifyStreamAsync);
                    await this.endpoint.RunEndpointAsync(async () => await RunEndpointAsync(endpoint));
                }
                catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                {
                    // Ingore, and try again.
                    Debug.WriteLine(ex.ToString());
                }
            }
        }

        private async Task<Stream> ModifyStreamAsync(NetworkStream ns)
        {
            if (this.useSsl)
            {
                var ssl = new SslStream(ns);
                await ssl.AuthenticateAsClientAsync(this.hostname, new X509CertificateCollection(),
                    Constants.sslProtocols, false);
                return ssl;
            }
            else
            {
                return ns;
            }
        }

        private async Task RunPingTaskAsync(SemaphoreSlim pingTimerSemaphore, TcpClientFramingEndpoint endpoint)
        {
            while (true)
            {
                bool exit = await pingTimerSemaphore.WaitAsync(60000);
                if (exit)
                    return;

                endpoint.SendMessageByQueue(new byte[] { 0xFF });
            }
        }

        private async Task RunEndpointAsync(TcpClientFramingEndpoint endpoint)
        {
            using (var pingTimerSemaphore = new SemaphoreSlim(0))
            {
                var pingTimerTask = Task.Run(async () => await RunPingTaskAsync(pingTimerSemaphore, endpoint));
                try
                {
                    while (true)
                    {
                        var packet = await endpoint.ReceiveNextPacketAsync(MaxReceivePacketSize);
                        if (packet == null)
                            return;

                        // TODO
                    }
                }
                finally
                {
                    HandleRemoteClientUnavailable();

                    pingTimerSemaphore.Release();
                    pingTimerTask.Wait();
                }
            }
        }

        private void HandleRemoteClientAvailable()
        {
            if (!this.remoteClientAvailable)
            {
                this.remoteClientAvailable = true;

                if (this.firstClientPortsAndRemoteHostnames != null)
                {
                    this.currentServer = new FirstClientServer(this.firstClientPortsAndRemoteHostnames, this.endpoint, AcceptFirstClientServerClient);
                    this.currentServer.Start();
                }
            }
        }

        private void HandleRemoteClientUnavailable()
        {
            if (this.remoteClientAvailable)
            {
                this.remoteClientAvailable = false;

                if (this.firstClientPortsAndRemoteHostnames != null)
                {
                    this.currentServer.Stop();
                    this.currentServer = null;
                }

                // TODO Close Connections
            }
        }

        private void AcceptFirstClientServerClient(TcpClient client, KeyValuePair<int, string> portAndRemoteHost)
        {

        }
    }
}
