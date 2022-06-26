using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SimpleSocketClient;

using TcpTunnel.Networking;
using TcpTunnel.Utils;

namespace TcpTunnel.Proxy;

/**
 * PROXY-TO-PROXY COMMUNICATION:
 * 8 bytes ConnectionID + 0x00 + 4 bytes port + utf-8 string Host: Open a new connection.
 * 8 bytes ConnectionID + 0x01 + arbitrary bytes: Transmit data packet or shutdown the connection if arbitrary bytes's length is 0.
 * 8 bytes ConnectionID + 0x02: Abort the connection.
 * 8 bytes ConnectionID + 0x03 + 4 bytes window size: Update receive window.
 */
public class Proxy : IInstance
{
    // The max message size is defined by the receive buffer size (32 KiB) plus
    // the additional data, which are just a few bytes. Therefore, 512 KiB should
    // be more than enough.
    public const int MaxReceiveMessageSize = 512 * 1024;

    private readonly object syncRoot = new();

    private readonly string hostname;
    private readonly int port;
    private readonly bool useSsl;
    private readonly int sessionId;
    private readonly ReadOnlyMemory<byte> sessionPasswordBytes;

    private readonly Action<string>? logger;

    /// <summary>
    /// If this proxy is a proxy-server, contains the connection descriptors
    /// for which <see cref="TcpListener"/>s are created.
    /// </summary>
    private readonly IReadOnlyList<ProxyServerConnectionDescriptor>? proxyServerConnectionDescriptors;

    private Task? readTask;
    private TcpClientFramingEndpoint? tcpEndpoint;
    private bool stopped;

    private ProxyServerListener? listener;

    /// <summary>
    /// The dictionary of active connections.
    /// </summary>
    private readonly Dictionary<long /* proxyId */,
        Dictionary<long /* connectionId */, ProxyTunnelConnection>> activePartnerProxiesAndConnections = new();

    public Proxy(
        string hostname,
        int port,
        bool useSsl,
        int sessionId,
        ReadOnlyMemory<byte> sessionPasswordBytes,
        IReadOnlyList<ProxyServerConnectionDescriptor>? proxyServerConnectionDescriptors,
        Action<string>? logger = null)
    {
        this.hostname = hostname;
        this.port = port;
        this.useSsl = useSsl;
        this.sessionId = sessionId;
        this.sessionPasswordBytes = sessionPasswordBytes;
        this.proxyServerConnectionDescriptors = proxyServerConnectionDescriptors;
        this.logger = logger;
    }

    private static (byte[] messageToSend, Memory<byte> coreMessage) PreparePartnerProxyMessage(
        int length,
        long? remoteProxyId)
    {
        var message = new byte[1 + (remoteProxyId is not null ? sizeof(long) : 0) + length];

        int pos = 0;
        message[pos++] = Constants.TypeProxyToProxyCommunication;

        if (remoteProxyId is not null)
        {
            BinaryPrimitives.WriteInt64BigEndian(message.AsSpan()[pos..], remoteProxyId.Value);
            pos += sizeof(long);
        }

        var coreMessage = message.AsMemory()[pos..][..length];
        return (message, coreMessage);
    }

    private static bool TryDecodePartnerProxyMessage(
        Memory<byte> message,
        bool containsPartnerProxyId,
        out Memory<byte> coreMessage,
        out long? partnerProxyId)
    {
        if (!(message.Length > 0 && message.Span[0] is Constants.TypeProxyToProxyCommunication))
        {
            coreMessage = default;
            partnerProxyId = null;
            return false;
        }

        coreMessage = message[1..];
        partnerProxyId = null;

        if (containsPartnerProxyId)
        {
            if (coreMessage.Length < sizeof(long))
                throw new InvalidDataException();

            partnerProxyId = BinaryPrimitives.ReadInt64BigEndian(coreMessage.Span);
            coreMessage = coreMessage[sizeof(long)..];
        }

        return true;
    }

    public void Start()
    {
        if (this.proxyServerConnectionDescriptors is not null)
        {
            // First, start the listener to ensure we can actually listen on all specified ports.
            this.listener = new ProxyServerListener(
                this.proxyServerConnectionDescriptors,
                (connectionId, client, portAndRemoteHost) => this.AcceptProxyServerClient(
                    connectionId,
                    client,
                    portAndRemoteHost));

            this.listener.Start();
        }

        this.readTask = ExceptionUtils.StartTask(this.RunReadTaskAsync);
    }

    public void Stop()
    {
        if (this.readTask is null)
            throw new InvalidOperationException();

        if (this.listener is not null)
            this.listener.Stop();

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
                            // algorithm, disable delayed ACKs, and enable TCP keep-alive.
                            SocketConfigurator.ConfigureSocket(client.Client, enableKeepAlive: true);

                            wasConnected = true;

                            this.logger?.Invoke(
                                $"Connection established to gateway " +
                                $"'{this.hostname}:{this.port.ToString(CultureInfo.InvariantCulture)}'. " +
                                $"Authenticating for Session ID '{this.sessionId.ToString(CultureInfo.InvariantCulture)}' " +
                                $"({(this.proxyServerConnectionDescriptors is not null ? "proxy-server" : "proxy-client")})...");
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
                        this.logger?.Invoke($"Connection to gateway lost. Reconnecting...");
                }
            }
            catch
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

        return null;
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
        var pingTimerTask = default(Task);

        bool isProxyClient = this.proxyServerConnectionDescriptors is null;

        try
        {
            // Send the login string.
            var loginString = new byte[2 + Constants.loginPrerequisiteBytes.Length +
                sizeof(int) + this.sessionPasswordBytes.Length];

            loginString[0] = 0x00;
            loginString[1] = this.proxyServerConnectionDescriptors is not null ? (byte)0x01 : (byte)0x00;

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
                var packet = await endpoint.ReceiveMessageAsync(MaxReceiveMessageSize);
                if (packet is null)
                    return;

                var packetBuffer = packet.Value.Buffer;

                if (packetBuffer.Length >= 2 && packetBuffer.Span[0] is 0x01)
                {
                    bool authenticationSucceeded = packetBuffer.Span[1] is 0x01;

                    this.logger?.Invoke(
                        $"Authentication: " +
                        $"{(authenticationSucceeded ? "Succeeded" : "Failed")}.");
                }
                else if (packetBuffer.Length >= 2 + (isProxyClient ? sizeof(long) : 0) &&
                    packetBuffer.Span[0] is 0x02)
                {
                    // New Session Status.
                    packetBuffer = packetBuffer[1..];

                    long? partnerProxyId = null;
                    if (isProxyClient)
                    {
                        partnerProxyId = BinaryPrimitives.ReadInt64BigEndian(packetBuffer.Span);
                        packetBuffer = packetBuffer[sizeof(long)..];

                        if (partnerProxyId is Constants.ProxyClientId)
                            throw new InvalidDataException();
                    }

                    bool partnerProxyAvailable = packetBuffer.Span[0] is 0x01;
                    this.logger?.Invoke(
                        $"Session Update: Partner Proxy Available{(partnerProxyId is not null ? $" [ID {partnerProxyId.Value.ToString(CultureInfo.InvariantCulture)}]" : "")}: " +
                        $"{(partnerProxyAvailable ? "Yes" : "No")}.");

                    // We always first need to treat this as the partner proxy
                    // being unavailable, because the gateway will send this only
                    // once when the partner proxy is replaced.
                    await this.HandlePartnerProxyUnavailableAsync(partnerProxyId ?? Constants.ProxyClientId);

                    if (partnerProxyAvailable)
                        this.HandlePartnerProxyAvailable(endpoint, partnerProxyId ?? Constants.ProxyClientId);
                }
                else if (TryDecodePartnerProxyMessage(
                    packetBuffer,
                    isProxyClient,
                    out var coreMessage,
                    out long? partnerProxyIdNullable))
                {
                    // Proxy to proxy communication.
                    if (partnerProxyIdNullable is { } value && value is Constants.ProxyClientId)
                        throw new InvalidDataException();

                    long partnerProxyId = partnerProxyIdNullable ?? 0;

                    // We don't need a lock to access the dictionary here since it is
                    // only modified by us (the receiver task). We only need a lock to
                    // change it, and when reading it from another task.
                    if (!this.activePartnerProxiesAndConnections.TryGetValue(
                        partnerProxyId,
                        out var activeConnections))
                        throw new InvalidDataException();

                    long connectionId = BinaryPrimitives.ReadInt64BigEndian(coreMessage.Span);
                    coreMessage = coreMessage[sizeof(long)..];

                    if (coreMessage.Length >= +1 + sizeof(int) &&
                        coreMessage.Span[0] is 0x00 &&
                        isProxyClient)
                    {
                        // Open a new connection, if we are the proxy-client.
                        int port = BinaryPrimitives.ReadInt32BigEndian(
                            coreMessage.Span[1..]);

                        string hostname = Encoding.UTF8.GetString(
                            coreMessage.Span[(1 + sizeof(int))..]);

                        var remoteClient = new TcpClient();

                        lock (this.syncRoot)
                        {
                            this.StartTcpTunnelConnection(
                                endpoint,
                                partnerProxyId,
                                activeConnections,
                                connectionId,
                                remoteClient,
                                async cancellationToken =>
                                {
                                    await remoteClient.ConnectAsync(hostname, port, cancellationToken);

                                    // After the socket is connected, configure it to disable the Nagle
                                    // algorithm, disable delayed ACKs, and enable TCP keep-alive.
                                    SocketConfigurator.ConfigureSocket(
                                        remoteClient.Client,
                                        enableKeepAlive: true);
                                });
                        }
                    }
                    else
                    {
                        ProxyTunnelConnection? connection;
                        lock (this.syncRoot)
                        {
                            // We might fail to find the connectionId if the connection
                            // was already fully closed.
                            activeConnections.TryGetValue(connectionId, out connection);
                        }

                        if (connection is not null)
                        {
                            if (coreMessage.Length >= 1 &&
                                coreMessage.Span[0] is 0x01)
                            {
                                // Transmit the data packet (or shutdown the connection if
                                // the length is 0).
                                // Need to copy the array because the caller might reuse
                                // the packet array.
                                var newPacket = new byte[coreMessage.Length - 1];
                                coreMessage[1..].CopyTo(newPacket);

                                try
                                {
                                    connection.EnqueueTransmitData(newPacket);
                                }
                                catch (InvalidOperationException)
                                {
                                    // The data to be queued would exceed the initial window
                                    // size, which cannot happen if the partner proxy works
                                    // correctly. Therefore, abort the connection.
                                    // By waiting for StopAsync(), we ensure that we don't
                                    // process any further commands for that connection.
                                    await connection.StopAsync();
                                }
                            }
                            else if (coreMessage.Length >= 1 &&
                                coreMessage.Span[0] is 0x02)
                            {
                                // Abort the connection, which will reset it immediately. We
                                // use StopAsync() to ensure the connection is removed from
                                // the list of active connections before we continue.
                                await connection.StopAsync();
                            }
                            else if (coreMessage.Length >= sizeof(int) &&
                                coreMessage.Span[0] is 0x03)
                            {
                                // Update the receive window size.
                                int windowSize = BinaryPrimitives.ReadInt32BigEndian(
                                    coreMessage.Span[1..]);

                                connection.UpdateReceiveWindow(windowSize);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            // Need to copy the keys list since it will be modified by the callee.
            foreach (long partnerProxyId in this.activePartnerProxiesAndConnections.Keys.ToArray())
                await this.HandlePartnerProxyUnavailableAsync(partnerProxyId);

            pingTimerSemaphore.Release();
            await (pingTimerTask ?? Task.CompletedTask);
        }
    }

    private void StartTcpTunnelConnection(
        TcpClientFramingEndpoint endpoint,
        long partnerProxyId,
        Dictionary<long, ProxyTunnelConnection> activeConnections,
        long connectionId,
        TcpClient remoteClient,
        Func<CancellationToken, ValueTask>? connectHandler = null)
    {
        Debug.Assert(Monitor.IsEntered(this.syncRoot));

        var connection = new ProxyTunnelConnection(
            remoteClient,
            connectHandler,
            receiveBuffer =>
            {
                // Forward the data packet.
                int length = sizeof(long) + 1 + receiveBuffer.Length;

                var (message, coreMessage) = PreparePartnerProxyMessage(
                    length,
                    this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                BinaryPrimitives.WriteInt64BigEndian(coreMessage.Span, connectionId);
                int pos = sizeof(long);

                coreMessage.Span[pos++] = 0x01;
                receiveBuffer.CopyTo(coreMessage[pos..]);
                pos += receiveBuffer.Length;

                endpoint.SendMessageByQueue(message);
            },
            window =>
            {
                // Forward the transmit window update.
                int length = sizeof(long) + 1 + sizeof(int);

                var (message, coreMessage) = PreparePartnerProxyMessage(
                    length,
                    this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                BinaryPrimitives.WriteInt64BigEndian(coreMessage.Span, connectionId);
                int pos = sizeof(long);

                coreMessage.Span[pos++] = 0x03;
                BinaryPrimitives.WriteInt32BigEndian(coreMessage.Span[pos..], window);
                pos += sizeof(int);

                endpoint.SendMessageByQueue(message);
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
                    activeConnections.Remove(connectionId);
                }

                if (isAbort)
                {
                    // Notify the partner that the connection was aborted.
                    int length = sizeof(long) + 1;

                    var (message, coreMessage) = PreparePartnerProxyMessage(
                        length,
                        this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                    BinaryPrimitives.WriteInt64BigEndian(coreMessage.Span, connectionId);
                    int pos = sizeof(long);

                    coreMessage.Span[pos++] = 0x02;

                    endpoint.SendMessageByQueue(message);
                }
            });

        // Add the connection and start it.
        activeConnections.Add(connectionId, connection);
        connection.Start();
    }

    private void HandlePartnerProxyAvailable(TcpClientFramingEndpoint endpoint, long partnerProxyId)
    {
        lock (this.syncRoot)
        {
            if (!this.activePartnerProxiesAndConnections.TryAdd(
                partnerProxyId,
                new Dictionary<long, ProxyTunnelConnection>()))
                throw new InvalidDataException();

            if (this.proxyServerConnectionDescriptors is not null)
            {
                // Acknowledge the new session iteration. Sending this message must be
                // done within the lock, as otherwise we might already start to send
                // create connection messages (through the listener) which the tunnel
                // gateway would then ignore.
                var response = new byte[] { 0x03 };
                endpoint.SendMessageByQueue(response);
            }
        }
    }

    private async ValueTask HandlePartnerProxyUnavailableAsync(long partnerProxyId)
    {
        if (this.activePartnerProxiesAndConnections.TryGetValue(
            partnerProxyId,
            out var activeConnections))
        {
            // Close the connections.
            // We need to copy the list since we wait for the connections to exit,
            // and they might want to remove themselves from the list in the
            // meanwhile.
            var connectionsToWait = new List<ProxyTunnelConnection>();

            lock (this.syncRoot)
            {
                // After we leave the lock, the listener won't add any more
                // connections to the list until we call HandlePartnerProxyAvailable
                // again.
                this.activePartnerProxiesAndConnections.Remove(partnerProxyId);
                connectionsToWait.AddRange(activeConnections.Values);
            }

            foreach (var pair in connectionsToWait)
                await pair.StopAsync();
        }
    }

    private void AcceptProxyServerClient(
        long connectionId,
        TcpClient client,
        ProxyServerConnectionDescriptor descriptor)
    {
        // After the socket is connected, configure it to disable the Nagle
        // algorithm, disable delayed ACKs, and enable TCP keep-alive.
        SocketConfigurator.ConfigureSocket(client.Client, enableKeepAlive: true);

        lock (this.syncRoot)
        {
            if (!this.activePartnerProxiesAndConnections.TryGetValue(
                Constants.ProxyClientId,
                out var activeConnections))
            {
                // When the partner proxy is not available, immediately abort the accepted
                // connection.
                try
                {
                    client.Client.Close(0);
                    client.Dispose();
                }
                catch
                {
                    // Ignore.
                }

                return;
            }

            // Send the create connection message before we add the connection to our list
            // of active connections and start it. Otherwise, if we sent it after that, it
            // could happen that the partner proxy would receive message for the connection
            // before it actually received the create connection message.
            // Note that sending the message and adding+starting the connection needs to be
            // done in the same lock, as otherwise it could happen that we would aleady receive
            // messages for the connection but wouldn't find it in the list.
            var hostnameBytes = Encoding.UTF8.GetBytes(descriptor.RemoteHost);
            int responseLength = sizeof(long) + 1 + sizeof(int) + hostnameBytes.Length;

            var (message, coreMessage) = PreparePartnerProxyMessage(responseLength, null);

            BinaryPrimitives.WriteInt64BigEndian(coreMessage.Span, connectionId);
            coreMessage.Span[sizeof(long)] = 0x00;

            BinaryPrimitives.WriteInt32BigEndian(
                coreMessage.Span[(sizeof(long) + 1)..],
                descriptor.RemotePort);

            hostnameBytes.CopyTo(coreMessage.Span[(sizeof(long) + 1 + sizeof(int))..]);

            // Send the message. From that point, our receiver task might already receive
            // events for the connection, which then need to wait for the lock.
            this.tcpEndpoint!.SendMessageByQueue(message);

            // Add the connection to the list and start it.
            this.StartTcpTunnelConnection(
                this.tcpEndpoint!,
                Constants.ProxyClientId,
                activeConnections,
                connectionId,
                client);
        }
    }
}
