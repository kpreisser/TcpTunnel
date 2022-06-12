using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SimpleSocketClient;

using TcpTunnel.Networking;
using TcpTunnel.Utils;

namespace TcpTunnel.Client;

/**
 * CLIENT-TO-CLIENT COMMUNICATION:
 * 8 bytes ConnectionID + 0x00 + 4 bytes port + utf-8 string Host: Open a new connection.
 * 8 bytes ConnectionID + 0x01 + arbitrary bytes: Transmit data packet or shutdown the connection if arbitrary bytes's length is 0.
 * 8 bytes ConnectionID + 0x02: Abort the connection.
 * 8 bytes ConnectionID + 0x03 + 4 bytes window size: Update receive window.
 */
public class TcpTunnelClient
{
    public const int MaxReceivePacketSize = 2 * 1024 * 1024;

    private readonly object syncRoot = new();

    private readonly string hostname;
    private readonly int port;
    private readonly bool useSsl;
    private readonly int sessionId;
    private readonly ReadOnlyMemory<byte> sessionPasswordBytes;

    private readonly Action<string>? logger;

    /// <summary>
    /// The first client is the one that provides server functionality by listening
    /// on specific ports.
    /// </summary>
    private readonly IReadOnlyList<TcpTunnelConnectionDescriptor>? firstClientConnectionDescriptors;

    private Task? readTask;
    private TcpClientFramingEndpoint? tcpEndpoint;
    private bool stopped;

    private bool remoteClientAvailable;
    private FirstClientServer? currentServer;

    /// <summary>
    /// The dictionary of active connections.
    /// This dictionary needs to be accesses with a lock, because it it may also be
    /// accessed from the TcpTunnelConnection's receive tasks.
    /// </summary>
    private readonly Dictionary<long, TcpTunnelConnection> activeConnections = new();

    public TcpTunnelClient(
        string hostname,
        int port,
        bool useSsl,
        int sessionId,
        ReadOnlyMemory<byte> sessionPasswordBytes,
        IReadOnlyList<TcpTunnelConnectionDescriptor>? firstClientConnectionDescriptors,
        Action<string>? logger = null)
    {
        this.hostname = hostname;
        this.port = port;
        this.useSsl = useSsl;
        this.sessionId = sessionId;
        this.sessionPasswordBytes = sessionPasswordBytes;
        this.firstClientConnectionDescriptors = firstClientConnectionDescriptors;
        this.logger = logger;
    }

    public void Start()
    {
        this.readTask = ExceptionUtils.StartTask(this.RunReadTaskAsync);
    }

    public void Stop()
    {
        if (this.readTask is null)
            throw new InvalidOperationException();

        lock (this.syncRoot)
        {
            this.stopped = true;

            if (this.tcpEndpoint is not null)
                this.tcpEndpoint.Cancel();
        }

        this.readTask.Wait();
    }

    private async Task RunReadTaskAsync()
    {
        this.logger?.Invoke($"Connecting...");

        while (true)
        {
            lock (this.syncRoot)
            {
                if (this.stopped)
                    return;
            }

            try
            {
                var client = new TcpClient();

                bool wasConnected = false; 
                try
                {
                    var tcpEndpoint = default(TcpClientFramingEndpoint);

                    tcpEndpoint = new TcpClientFramingEndpoint(
                        client,
                        useSendQueue: true,
                        usePingTimer: false,
                        connectHandler: async cancellationToken =>
                        {
                            // Beginning from this stage, the endpoint can be canceled, so we
                            // can now set it.
                            lock (this.syncRoot)
                            {
                                if (this.stopped)
                                    throw new OperationCanceledException();

                                this.tcpEndpoint = tcpEndpoint!;
                            }

                            await client.ConnectAsync(this.hostname, this.port, cancellationToken);

                            // After the socket is connected, configure it to disable the Nagle
                            // algorithm and delayed ACKs (and maybe enable TCP keep-alive in the
                            // future).
                            SocketConfigurator.ConfigureSocket(client.Client);

                            wasConnected = true;

                            this.logger?.Invoke(
                                $"Connection established to gateway " +
                                $"'{this.hostname}:{this.port.ToString(CultureInfo.InvariantCulture)}'. " +
                                $"Authenticating...");
                        },
                        closeHandler: () =>
                        {
                            // Once this handler returns, the endpoint may no longer
                            // be canceled, so clear the instance.
                            lock (this.syncRoot)
                            {
                                this.tcpEndpoint = null;
                            }
                        },
                        streamModifier: this.ModifyStreamAsync);

                    await tcpEndpoint.RunEndpointAsync(() => this.RunEndpointAsync(tcpEndpoint));
                }
                finally
                {
                    client.Dispose();

                    if (wasConnected)
                        this.logger?.Invoke($"Connection to gateway closed. Reconnecting...");
                }
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                // Ingore, and try again.
            }

            // Wait. TODO: Use a semaphore to exit faster.
            await Task.Delay(2000);
        }
    }

    private async ValueTask<Stream?> ModifyStreamAsync(
        NetworkStream networkStream,
        CancellationToken cancellationToken)
    {
        if (this.useSsl)
        {
            var sslStream = new SslStream(networkStream);
            try
            {
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions()
                    {
                        TargetHost = this.hostname,
                        EnabledSslProtocols = Constants.sslProtocols,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    },
                    cancellationToken);
            }
            catch
            {
                await sslStream.DisposeAsync();
                throw;
            }

            return sslStream;
        }
        else
        {
            return null;
        }
    }

    private async Task RunPingTaskAsync(
        SemaphoreSlim pingTimerSemaphore,
        TcpClientFramingEndpoint endpoint)
    {
        while (true)
        {
            bool exit = await pingTimerSemaphore.WaitAsync(30000);
            if (exit)
                return;

            endpoint.SendMessageByQueue(new byte[] { 0xFF });
        }
    }

    private async Task RunEndpointAsync(TcpClientFramingEndpoint endpoint)
    {
        using var pingTimerSemaphore = new SemaphoreSlim(0);
        Task? pingTimerTask = null;

        try
        {
            // Send the login string.
            var loginString = new byte[2 + Constants.loginPrerequisiteBytes.Length +
                sizeof(int) + this.sessionPasswordBytes.Length];

            loginString[0] = 0x00;
            loginString[1] = this.firstClientConnectionDescriptors is not null ? (byte)0x00 : (byte)0x01;

            Constants.loginPrerequisiteBytes.CopyTo(loginString.AsMemory()[2..]);
            BinaryPrimitives.WriteInt32BigEndian(
                loginString.AsSpan()[(2 + Constants.loginPrerequisiteBytes.Length)..],
                this.sessionId);

            this.sessionPasswordBytes.Span.CopyTo(
                loginString.AsSpan()[(2 + Constants.loginPrerequisiteBytes.Length + sizeof(int))..]);

            endpoint.SendMessageByQueue(loginString);

            // Start the ping timer task, then receive packets.
            pingTimerTask = Task.Run(() => this.RunPingTaskAsync(pingTimerSemaphore, endpoint));

            while (true)
            {
                var packet = await endpoint.ReceiveMessageAsync(MaxReceivePacketSize);
                if (packet is null)
                    return;

                var packetBuffer = packet.Value.Buffer;

                if (packetBuffer.Length >= 2 && packetBuffer.Span[0] is 0x02)
                {
                    bool remoteClientAvailable = packetBuffer.Span[1] is 0x01;
                    this.logger?.Invoke(
                        $"Session Update: Remote Client Available: " +
                        $"{(remoteClientAvailable ? "Yes" : "No")}.");

                    // New Session Status.
                    // We always first need to treat this as the partner client
                    // being unavailable, because the server will send this only
                    // once when the partner client is replaced.
                    await this.HandlePartnerClientUnavailableAsync();

                    if (remoteClientAvailable)
                        this.HandlePartnerClientAvailable(endpoint);
                }
                else if (packetBuffer.Length >= 1 + sizeof(long) &&
                    packetBuffer.Span[0] is Constants.TypeClientToClientCommunication)
                {
                    // Client to client communication.
                    long connectionId = BinaryPrimitives.ReadInt64BigEndian(packetBuffer.Span[1..]);

                    if (packetBuffer.Length >= 1 + sizeof(long) + 1 + sizeof(int) &&
                        packetBuffer.Span[1 + sizeof(long)] is 0x00 &&
                        this.firstClientConnectionDescriptors is null)
                    {
                        // Open a new connection, if we are not the first client.
                        int port = BinaryPrimitives.ReadInt32BigEndian(
                            packetBuffer.Span[(1 + sizeof(long) + 1)..]);

                        string hostname = Encoding.UTF8.GetString(
                            packetBuffer.Span[(1 + sizeof(long) + 1 + sizeof(int))..]);

                        var remoteClient = new TcpClient();

                        var connection = this.AddTcpTunnelConnection(
                            endpoint,
                            connectionId,
                            remoteClient,
                            async cancellationToken =>
                            {
                                await remoteClient.ConnectAsync(hostname, port, cancellationToken);

                                // After the socket is connected, configure it to disable the Nagle
                                // algorithm and delayed ACKs (and maybe enable TCP keep-alive in the
                                // future).
                                SocketConfigurator.ConfigureSocket(remoteClient.Client);
                            });

                        connection.Start();
                    }
                    else
                    {
                        TcpTunnelConnection? connection;
                        lock (this.syncRoot)
                        {
                            // We might fail to find the connectionId if the connection
                            // was already fully closed.
                            this.activeConnections.TryGetValue(connectionId, out connection);
                        }

                        if (connection is not null)
                        {
                            if (packetBuffer.Length >= 1 + sizeof(long) + 1 &&
                                packetBuffer.Span[1 + sizeof(long)] is 0x01)
                            {
                                // Transmit the data packet (or shutdown the connection if
                                // the length is 0).
                                // Need to copy the array because the caller might reuse
                                // the packet array.
                                var newPacket = new byte[packetBuffer.Length - (1 + sizeof(long) + 1)];
                                packetBuffer[(1 + sizeof(long) + 1)..].CopyTo(newPacket);

                                connection.EnqueuePacket(newPacket);
                            }
                            else if (packetBuffer.Length >= 1 + sizeof(long) + 1 &&
                                packetBuffer.Span[1 + sizeof(long)] is 0x02)
                            {
                                // Abort the connection, which will reset it immediately. We
                                // use StopAsync() to ensure the connection is removed from
                                // the list of active connections before we continue.
                                await connection.StopAsync();
                            }
                            else if (packetBuffer.Length >= 1 + sizeof(long) + 1 + sizeof(int) &&
                                packetBuffer.Span[1 + sizeof(long)] is 0x03)
                            {
                                // Update the receive window size.
                                int windowSize = BinaryPrimitives.ReadInt32BigEndian(
                                    packetBuffer.Span[(1 + sizeof(long) + 1)..]);

                                connection.UpdateReceiveWindow(windowSize);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            await this.HandlePartnerClientUnavailableAsync();

            pingTimerSemaphore.Release();
            await (pingTimerTask ?? Task.CompletedTask);
        }
    }

    private TcpTunnelConnection AddTcpTunnelConnection(
        TcpClientFramingEndpoint endpoint,
        long connectionId,
        TcpClient remoteClient,
        Func<CancellationToken, ValueTask>? connectHandler = null)
    {
        var connection = new TcpTunnelConnection(
            remoteClient,
            connectHandler,
            receiveBuffer =>
            {
                // Forward the data packet.
                var response = new byte[1 + sizeof(long) + 1 + receiveBuffer.Length];

                response[0] = Constants.TypeClientToClientCommunication;
                BinaryPrimitives.WriteInt64BigEndian(response.AsSpan()[1..], connectionId);
                response[1 + sizeof(long)] = 0x01;
                receiveBuffer.CopyTo(response.AsMemory()[(1 + sizeof(long) + 1)..]);

                endpoint.SendMessageByQueue(response);
            },
            window =>
            {
                // Forward the transmit window update.
                var response = new byte[1 + sizeof(long) + 1 + sizeof(int)];

                response[0] = Constants.TypeClientToClientCommunication;
                BinaryPrimitives.WriteInt64BigEndian(response.AsSpan()[1..], connectionId);
                response[1 + sizeof(long)] = 0x03;
                BinaryPrimitives.WriteInt32BigEndian(response.AsSpan()[(1 + sizeof(long) + 1)..], window);

                endpoint.SendMessageByQueue(response);
            },
            isAbort =>
            {
                lock (this.syncRoot)
                {
                    // The connection is finished, so remove it. Note that the receiveTask
                    // is still running (we are being called from it, so we can't wait for
                    // it by calling StopAsync()), but at this stage the connection is
                    // considered to be finished and it doesn't have any more resources open,
                    // so it is OK to just remove it.
                    this.activeConnections.Remove(connectionId);
                }

                if (isAbort)
                {
                    // Notify the partner that the connection was aborted.
                    var response = new byte[1 + sizeof(long) + 1];

                    response[0] = Constants.TypeClientToClientCommunication;
                    BinaryPrimitives.WriteInt64BigEndian(response.AsSpan()[1..], connectionId);
                    response[1 + sizeof(long)] = 0x02;

                    endpoint.SendMessageByQueue(response);
                }
            });

        // Add the connection.
        lock (this.syncRoot)
        {
            this.activeConnections.Add(connectionId, connection);
        }

        // Return it so that the caller can start it. We cannot start it here already,
        // since the caller might first need to send a message to the partner client
        // to inform it about the new connection, and it cannot do that before calling
        // this method as otherwise the caller might already receive events for the
        // connection before we actually added it to the list of active connections.
        return connection;
    }

    private void HandlePartnerClientAvailable(TcpClientFramingEndpoint endpoint)
    {
        this.remoteClientAvailable = true;

        // Acknowledge the new session iteration. Sending this message must be done
        // before starting the FirstClientServer.
        var response = new byte[] { 0x03 };
        endpoint.SendMessageByQueue(response);

        if (this.firstClientConnectionDescriptors is not null)
        {
            this.currentServer = new FirstClientServer(
                this.firstClientConnectionDescriptors,
                (connectionId, client, portAndRemoteHost) => this.AcceptFirstClientServerClient(
                    endpoint,
                    connectionId,
                    client,
                    portAndRemoteHost));

            this.currentServer.Start();
        }
    }

    private async ValueTask HandlePartnerClientUnavailableAsync()
    {
        if (this.remoteClientAvailable)
        {
            this.remoteClientAvailable = false;

            if (this.firstClientConnectionDescriptors is not null)
            {
                // Wait until the server no longer accepts new connections.
                await this.currentServer!.StopAsync();
                this.currentServer = null;
            }

            // Close the connections.
            // We need to copy the list since we wait for the connections to exit,
            // and they might want to remove themselves from the list in the
            // meanwhile.
            var connectionsToWait = new List<TcpTunnelConnection>();
            lock (this.syncRoot)
            {
                connectionsToWait.AddRange(this.activeConnections.Values);
            }

            // After waiting for all connections to stop, the activeConnections
            // dictionary will be empty.
            foreach (var pair in connectionsToWait)
                await pair.StopAsync();
        }
    }

    private void AcceptFirstClientServerClient(
        TcpClientFramingEndpoint endpoint,
        long connectionId,
        TcpClient client,
        TcpTunnelConnectionDescriptor descriptor)
    {
        // Create and add the connection to our list of active connections. We must start
        // it only AFTER sending the initialization message, because the connection runs
        // on another task and might already receive packets (which might then arrive
        // on the partner client before the initialization message arrives).
        var connection = this.AddTcpTunnelConnection(endpoint, connectionId, client);

        // Send the message not before we added the connection to our list of active
        // connections. Otherwise, if we sent it earlier, it could happen that the
        // partner client already sends events for the connection (like data packets),
        // but we would discard them since we wouldn't yet find it in the list.
        var hostnameBytes = Encoding.UTF8.GetBytes(descriptor.RemoteHost);
        var response = new byte[1 + sizeof(long) + 1 + sizeof(int) + hostnameBytes.Length];
        response[0] = Constants.TypeClientToClientCommunication;

        BinaryPrimitives.WriteInt64BigEndian(response.AsSpan()[1..], connectionId);
        response[1 + sizeof(long)] = 0x00;

        BinaryPrimitives.WriteInt32BigEndian(
            response.AsSpan()[(1 + sizeof(long) + 1)..],
            descriptor.RemotePort);

        hostnameBytes.CopyTo(response.AsSpan()[(1 + sizeof(long) + 1 + sizeof(int))..]);

        endpoint.SendMessageByQueue(response);

        // After sending the message, we can start the connection.
        connection.Start();
    }
}
