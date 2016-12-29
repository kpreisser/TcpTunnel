using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    /**
     * Client to Client communication:
     * 8 bytes ConnectionID + 0x00 + 4 bytes port + utf-8 string Host: Open a new connection.
     * 8 bytes ConnectionID + 0x01 + arbitrary bytes: Transmit data packet or shutdown the connection if arbitrary bytes's length is 0.
     * 8 bytes ConnectionID + 0x02 + 4 bytes window size: Update receive window.
     */
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
        private bool stopped;

        public object SyncRoot { get; } = new object();
        
        private bool remoteClientAvailable;
        private long currentIteration;
        private FirstClientServer currentServer;
        /// <summary>
        /// The dictionary of active connections. A connection is only removed if we manually abort them, or it the
        /// connection's receive handler is called with an null array.
        /// This list needs to be accesses with a lock, because it it may also be accessed from the TcpTunnelConnection's
        /// receive tasks.
        /// </summary>
        private IDictionary<long, TcpTunnelConnection> activeConnections =
            new SortedDictionary<long, TcpTunnelConnection>();

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

            this.readTask.Wait();
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
                    await endpoint.RunEndpointAsync(async () => await RunEndpointAsync(endpoint));
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
                Task pingTimerTask = null;
                try
                {
                    // Send the login string.
                    byte[] sessionPasswordBytes = Encoding.UTF8.GetBytes(this.sessionPassword);
                    byte[] loginString = new byte[3 + Constants.loginPrerequisiteBytes.Count 
                        + sizeof(int) + sessionPasswordBytes.Length];
                    loginString[0] = 0x00;
                    loginString[1] = 0x00;
                    loginString[2] = firstClientPortsAndRemoteHostnames == null ? (byte)0x00 : (byte)0x01;
                    Array.Copy(Constants.loginPrerequisiteBytes.Array, Constants.loginPrerequisiteBytes.Offset,
                        loginString, 3, Constants.loginPrerequisiteBytes.Count);
                    BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(this.sessionID),
                        loginString, 3 + Constants.loginPrerequisiteBytes.Count);
                    Array.Copy(sessionPasswordBytes, 0, loginString,
                        3 + Constants.loginPrerequisiteBytes.Count + sizeof(int), sessionPasswordBytes.Length);
                    endpoint.SendMessageByQueue(sessionPasswordBytes);

                    // Start the ping timer task, then receive packets.
                    pingTimerTask = Task.Run(async () => await RunPingTaskAsync(pingTimerSemaphore, endpoint));
                                    
                    while (true)
                    {
                        var packet = await endpoint.ReceiveNextPacketAsync(MaxReceivePacketSize);
                        if (packet == null)
                            return;

                        if (packet.RawBytes.Count >= 2 + 8 + 1
                            && packet.RawBytes.Array[packet.RawBytes.Offset + 0] == 0x00
                            && packet.RawBytes.Array[packet.RawBytes.Offset + 1] == 0x02)
                        {
                            // Session Status
                            HandleRemoteClientUnavailable();

                            long currentSessionIteration = IPAddress.NetworkToHostOrder(BitConverter.ToInt64(
                                packet.RawBytes.Array, packet.RawBytes.Offset + 2));
                            this.currentIteration = currentSessionIteration;
                            bool removeClientAvailable = packet.RawBytes.Array[packet.RawBytes.Offset + 2 + sizeof(long)] == 0x00;

                            if (removeClientAvailable)
                                HandleRemoteClientAvailable(endpoint, currentSessionIteration);
                        }
                        else if (packet.RawBytes.Count >= 1 + sizeof(long)
                            && packet.RawBytes.Array[packet.RawBytes.Offset + 0] == 0x01)
                        {
                            // Client to client communication.
                            long connectionID = IPAddress.NetworkToHostOrder(BitConverter.ToInt64(packet.RawBytes.Array,
                                packet.RawBytes.Offset + 1));

                            if (packet.RawBytes.Count >= 1 + sizeof(long) + 1 + sizeof(int) 
                                && packet.RawBytes.Array[packet.RawBytes.Offset + 1 + sizeof(long)] == 0x00
                                && firstClientPortsAndRemoteHostnames == null)
                            {
                                // Open a new connection, if we are not the first client.
                                int port = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(
                                    packet.RawBytes.Array, packet.RawBytes.Offset + 1 + sizeof(long) + 1));
                                string hostname = Encoding.UTF8.GetString(packet.RawBytes.Array,
                                    packet.RawBytes.Offset + 1 + sizeof(long) + 1 + sizeof(int),
                                    packet.RawBytes.Count - (1 + sizeof(long) + 1 + sizeof(int)));

                                var remoteClient = new TcpClient();
                                var connection = CreateTcpTunnelConnection(endpoint, this.currentIteration, connectionID,
                                    remoteClient, async () => await remoteClient.ConnectAsync(hostname, port));
                                connection.Start();

                                // Add the connection.
                                lock (this.SyncRoot)
                                {
                                    activeConnections.Add(connectionID, connection);
                                }                                
                            }
                            else if (packet.RawBytes.Count >= 1 + sizeof(long) + 1
                                && packet.RawBytes.Array[packet.RawBytes.Offset + 1 + sizeof(long)] == 0x01)
                            {
                                // Transmit the data packet (or shutdown the connection if the length is 0).
                                // Need to copy the array because the caller might reuse the packet array.
                                byte[] newPacket = new byte[packet.RawBytes.Count - 1 - sizeof(long) - 1];
                                Array.Copy(packet.RawBytes.Array, packet.RawBytes.Offset + 1 + sizeof(long) + 1,
                                    newPacket, 0, newPacket.Length);

                                TcpTunnelConnection connection;
                                lock (this.SyncRoot)
                                {
                                    if (!this.activeConnections.TryGetValue(connectionID, out connection))
                                        connection = null;
                                }
                                connection?.EnqueuePacket(new ArraySegment<byte>(newPacket));
                            }
                            else if (packet.RawBytes.Count >= 1 + sizeof(long) + 1 + 4
                                && packet.RawBytes.Array[packet.RawBytes.Offset + 1 + sizeof(long)] == 0x02)
                            {
                                // Update the receive window size.
                                int windowSize = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(packet.RawBytes.Array,
                                    packet.RawBytes.Offset + 1 + sizeof(long) + 1));
                                TcpTunnelConnection connection;
                                lock (this.SyncRoot)
                                {
                                    if (!this.activeConnections.TryGetValue(connectionID, out connection))
                                        connection = null;
                                }
                                connection?.UpdateReceiveWindow(windowSize);
                            }

                        }

                    }
                }
                finally
                {
                    HandleRemoteClientUnavailable();

                    pingTimerSemaphore.Release();
                    pingTimerTask?.Wait();
                }
            }
        }

        private TcpTunnelConnection CreateTcpTunnelConnection(TcpClientFramingEndpoint endpoint,
            long currentSessionIteration, long connectionID,
            TcpClient remoteClient, Func<Task> connectHandler = null)
        {
            return new TcpTunnelConnection(remoteClient,
                connectHandler,
                (receivePacket) =>
                {
                    // Received a packet.
                    byte[] response = new byte[1 + sizeof(long) + sizeof(long) + 1 + receivePacket.Count];
                    response[0] = 0x01;
                    BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(currentSessionIteration), response, 1);
                    BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(connectionID), response, 1 + sizeof(long));
                    response[1 + sizeof(long) + sizeof(long)] = 0x01;
                    Array.Copy(receivePacket.Array, receivePacket.Offset, response,
                        1 + sizeof(long) + sizeof(long) + 1, receivePacket.Count);

                    endpoint.SendMessageByQueue(response);

                    if (receivePacket.Count == 0)
                    {
                        // The connection has been closed, so remove it. The receive task might still run at this point,
                        // but it doesn't raise any more events, it already closed the remoteClient, and the transmit task 
                        // is also already finished at this point.
                        lock (this.SyncRoot)
                        {
                            activeConnections.Remove(connectionID);
                        }
                    }
                },
                (window) =>
                {
                    // Got a transmit window update.
                    byte[] response = new byte[1 + sizeof(long) + sizeof(long) + 1 + sizeof(int)];
                    response[0] = 0x01;
                    BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(currentSessionIteration), response, 1);
                    BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(connectionID), response, 1 + sizeof(long));
                    response[1 + sizeof(long) + sizeof(long)] = 0x02;
                    BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(window),
                        response, 1 + sizeof(long) + sizeof(long) + 1);

                    endpoint.SendMessageByQueue(response);
                });
        }

        private void HandleRemoteClientAvailable(TcpClientFramingEndpoint endpoint, long currentSessionIteration)
        {
            if (!this.remoteClientAvailable)
            {
                this.remoteClientAvailable = true;

                if (this.firstClientPortsAndRemoteHostnames != null)
                {
                    this.currentServer = new FirstClientServer(this.firstClientPortsAndRemoteHostnames, endpoint,
                        (connectionID, client, portAndRemoteHost) => AcceptFirstClientServerClient(
                            endpoint, currentSessionIteration, connectionID, client, portAndRemoteHost));
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

                // Close the connections.
                // We need to copy the list since we wait for the connections to exit, and they might want
                // to remove themselves from the list in the meanwhile.
                var connectionsToWait = new List<KeyValuePair<long, TcpTunnelConnection>>();
                lock (SyncRoot)
                {
                    connectionsToWait.AddRange(this.activeConnections);
                }

                foreach (var pair in connectionsToWait)
                    pair.Value.Stop();

                lock (SyncRoot)
                {
                    // Normally the dictionary should already be empty now.
                    Debug.WriteLine(this.activeConnections.Count);
                    this.activeConnections.Clear();
                }
            }
        }

        private void AcceptFirstClientServerClient(TcpClientFramingEndpoint endpoint,
            long currentSessionIteration, long connectionID,
            TcpClient client, KeyValuePair<int, string> portAndRemoteHost)
        {
            var connection = CreateTcpTunnelConnection(endpoint, currentSessionIteration, connectionID, client);
            lock (this.SyncRoot)
            {
                this.activeConnections.Add(connectionID, connection);
            }

            byte[] hostnameBytes = Encoding.UTF8.GetBytes(portAndRemoteHost.Value);
            var response = new byte[1 + sizeof(long) + sizeof(long) + 1 + sizeof(int) + hostnameBytes.Length];
            response[0] = 0x01;
            BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(currentSessionIteration), response, 1);
            BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(connectionID), response, 1 + sizeof(long));
            response[1 + sizeof(long) + sizeof(long)] = 0x00;
            BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(portAndRemoteHost.Key),
                response, 1 + sizeof(long) + sizeof(long) + 1);
            Array.Copy(hostnameBytes, 0, response,
                1 + sizeof(long) + sizeof(long) + 1 + sizeof(int), hostnameBytes.Length);

            endpoint.SendMessageByQueue(response);
        }
    }
}
