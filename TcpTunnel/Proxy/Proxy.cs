using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
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
public partial class Proxy : IInstance
{
    // The max message size is defined by the receive buffer size (32 KiB) plus
    // the additional data, which are just a few bytes. Therefore, 512 KiB should
    // be more than enough.
    public const int MaxReceiveMessageSize = 512 * 1024;

    private const int ProxyMessageTypeOpenConnection = 0x00;
    private const int ProxyMessageTypeData = 0x01;
    private const int ProxyMessageTypeAbortConnection = 0x02;
    private const int ProxyMessageTypeUpdateWindow = 0x03;

    private readonly object syncRoot = new();

    private readonly string gatewayHost;
    private readonly int gatewayPort;
    private readonly bool gatewayUseSsl;
    private readonly int sessionId;
    private readonly ReadOnlyMemory<byte> sessionPasswordBytes;

    private readonly Action<string>? logger;

    /// <summary>
    /// If not <c>null</c>, the current instance is a proxy-server and this list contains
    /// the connection descriptors for which <see cref="TcpListener"/>s are created.
    /// </summary>
    private readonly IReadOnlyList<ProxyServerConnectionDescriptor>? proxyServerConnectionDescriptors;

    /// <summary>
    /// If not <c>null</c> and the current instance is a proxy-client, contains target endpoints
    /// which are allowed. Otherwise, if this is <c>null</c>, all target endpoints are allowed.
    /// </summary>
    private readonly IReadOnlyList<(string host, int port)>? proxyClientAllowedTargetEndpoints;

    private Task? readTask;
    private CancellationTokenSource? readTaskCts;
    private TcpClientFramingEndpoint? tcpEndpoint;

    private ProxyServerListener? listener;

    /// <summary>
    /// The dictionary of active connections.
    /// </summary>
    private readonly Dictionary<long /* proxyId */,
        Dictionary<long /* connectionId */, ProxyTunnelConnection<TunnelConnectionData>>> activePartnerProxiesAndConnections = new();

    public Proxy(
        string gatewayHost,
        int gatewayPort,
        bool gatewayUseSsl,
        int sessionId,
        ReadOnlyMemory<byte> sessionPasswordBytes,
        IReadOnlyList<ProxyServerConnectionDescriptor>? proxyServerConnectionDescriptors,
        IReadOnlyList<(string host, int port)>? proxyClientAllowedTargetEndpoints,
        Action<string>? logger = null)
    {
        this.gatewayHost = gatewayHost;
        this.gatewayPort = gatewayPort;
        this.gatewayUseSsl = gatewayUseSsl;
        this.sessionId = sessionId;
        this.sessionPasswordBytes = sessionPasswordBytes;
        this.proxyServerConnectionDescriptors = proxyServerConnectionDescriptors;
        this.proxyClientAllowedTargetEndpoints = proxyClientAllowedTargetEndpoints;
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
                this.AcceptProxyServerClient);

            this.listener.Start();
        }

        this.readTaskCts = new CancellationTokenSource();
        this.readTask = ExceptionUtils.StartTask(() => this.RunReadTaskAsync(this.readTaskCts.Token));
    }

    public void Stop()
    {
        if (this.readTask is null)
            throw new InvalidOperationException();

        this.listener?.Stop();

        // Cancel the read task. This might call the task continuation inline,
        // which is OK as we will wait for the task anyway directly after that.
        this.readTaskCts!.CancelAndIgnoreAggregateException();

        this.readTask.GetAwaiter().GetResult();
        this.readTaskCts!.Dispose();
    }

    private async Task RunReadTaskAsync(CancellationToken readTaskCancellationToken)
    {
        this.logger?.Invoke($"Connecting to gateway...");

        string? lastConnectionError = null;

        try
        {
            while (true)
            {
                readTaskCancellationToken.ThrowIfCancellationRequested();

                var client = new TcpClient();

                bool wasConnected = false;
                var caughtException = default(Exception);
                try
                {
                    var tcpEndpoint = new TcpClientFramingEndpoint(
                        client,
                        useSendQueue: true,
                        usePingTimer: false,
                        connectHandler: async cancellationToken =>
                        {
                            // Note: We will log the "Connected" message in the stream modifier
                            // callback, because only after that the connection can considered
                            // to be fully established.
                            await client.ConnectAsync(
                                this.gatewayHost,
                                this.gatewayPort,
                                cancellationToken);

                            // After the socket is connected, configure it to disable the Nagle
                            // algorithm, disable delayed ACKs, and enable TCP keep-alive.
                            SocketConfigurator.ConfigureSocket(client.Client, enableKeepAlive: true);

                            // Use a smaller socket send buffer size to reduce buffer bloat.
                            client.Client.SendBufferSize = Constants.SocketSendBufferSize;
                        },
                        streamModifier: async (networkStream, cancellationToken) =>
                        {
                            var modifiedStream = default(Stream);

                            if (this.gatewayUseSsl)
                            {
                                var sslStream = new SslStream(networkStream);
                                try
                                {
                                    await sslStream.AuthenticateAsClientAsync(
                                        new SslClientAuthenticationOptions()
                                        {
                                            TargetHost = this.gatewayHost,
                                            EnabledSslProtocols = Constants.SslProtocols
                                        },
                                        cancellationToken);
                                }
                                catch (Exception ex) when (ex.CanCatch())
                                {
                                    await sslStream.DisposeAsync();
                                    throw;
                                }

                                modifiedStream = sslStream;
                            }

                            wasConnected = true;
                            this.logger?.Invoke(
                                $"Connection established to gateway " +
                                $"'{this.gatewayHost}:{this.gatewayPort.ToString(CultureInfo.InvariantCulture)}'. " +
                                $"Authenticating for Session ID '{this.sessionId.ToString(CultureInfo.InvariantCulture)}' " +
                                $"({(this.proxyServerConnectionDescriptors is not null ? "proxy-server" : "proxy-client")})...");

                            return modifiedStream;
                        });

                    await tcpEndpoint.RunEndpointAsync(
                        cancellationToken => this.RunEndpointAsync(tcpEndpoint, cancellationToken),
                        readTaskCancellationToken);
                }
                catch (Exception ex) when (ex.CanCatch())
                {
                    // Ignore, and try again. This includes the case when an
                    // OperationCanceledException was thrown above, which might be caused by
                    // the Endpoint in order to cancel the current connection.
                    // We check later if it was caused by us canceling the CTS.
                    caughtException = ex;
                }
                finally
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch (Exception ex) when (ex.CanCatch())
                    {
                        // Ignore
                    }
                }

                // Log the error if we lost connection, or if we couldn't connect and this
                // is a new error.
                string newConnectionError = caughtException?.Message ?? "Peer closed connection.";

                if (wasConnected)
                    this.logger?.Invoke($"Connection to gateway lost ({newConnectionError}). Reconnecting...");
                else if (newConnectionError != lastConnectionError)
                    this.logger?.Invoke($"Unable to connect to gateway ({newConnectionError}). Trying again...");

                lastConnectionError = newConnectionError;

                // Wait a bit.
                await Task.Delay(2000, readTaskCancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The proxy was stopped, so return.
            return;
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

    private async Task RunEndpointAsync(TcpClientFramingEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var pingTimerSemaphore = new SemaphoreSlim(0);
        var pingTimerTask = default(Task);

        bool isProxyClient = this.proxyServerConnectionDescriptors is null;

        // Beginning from this stage, the endpoint can be used, so we
        // can now set it.
        lock (this.syncRoot)
        {
            this.tcpEndpoint = endpoint;
        }

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
                var packet = await endpoint.ReceiveMessageAsync(MaxReceiveMessageSize, cancellationToken);

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
                    // once in case the partner proxy is replaced.
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

                    long partnerProxyId = partnerProxyIdNullable ?? Constants.ProxyClientId;

                    // We don't need a lock to access the dictionary here since it is
                    // only modified by us (the receiver task). We only need a lock to
                    // change it, and when reading it from another task.
                    if (!this.activePartnerProxiesAndConnections.TryGetValue(
                        partnerProxyId,
                        out var activeConnections))
                        throw new InvalidDataException();

                    long connectionId = BinaryPrimitives.ReadInt64BigEndian(coreMessage.Span);
                    coreMessage = coreMessage[sizeof(long)..];

                    if (coreMessage.Length >= 1 + sizeof(int) &&
                        coreMessage.Span[0] is ProxyMessageTypeOpenConnection &&
                        isProxyClient)
                    {
                        // Open a new connection, if we are the proxy-client.
                        int port = BinaryPrimitives.ReadInt32BigEndian(
                            coreMessage.Span[1..]);

                        string hostname = Encoding.UTF8.GetString(
                            coreMessage.Span[(1 + sizeof(int))..]);

                        // If the proxyClientAllowedTargetEndpoints list is specified, we need
                        // to check whether the target endpoint is allowed. Otherwise, we abort
                        // the connection.
                        if (this.proxyClientAllowedTargetEndpoints is not null &&
                            !this.proxyClientAllowedTargetEndpoints.Contains((hostname, port)))
                        {
                            // Notify the partner that the connection had to be aborted.
                            int length = sizeof(long) + 1;

                            var (messageToSend, coreMessageToSend) = PreparePartnerProxyMessage(
                                length,
                                partnerProxyId);

                            BinaryPrimitives.WriteInt64BigEndian(coreMessageToSend.Span, connectionId);
                            int pos = sizeof(long);

                            coreMessageToSend.Span[pos++] = ProxyMessageTypeAbortConnection;

                            endpoint.SendMessageByQueue(messageToSend);
                        }
                        else
                        {
                            lock (this.syncRoot)
                            {
                                if (activeConnections.ContainsKey(connectionId))
                                    throw new InvalidDataException();

                                var remoteClient = new TcpClient();

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

                                        // Additionally, use a smaller socket send buffer size to reduce
                                        // buffer bloat.
                                        remoteClient.Client.SendBufferSize = Constants.SocketSendBufferSize;
                                    });
                            }
                        }
                    }
                    else
                    {
                        ProxyTunnelConnection<TunnelConnectionData>? connection;
                        lock (this.syncRoot)
                        {
                            // We might fail to find the connectionId if the connection
                            // was already fully closed.
                            activeConnections.TryGetValue(connectionId, out connection);
                        }

                        if (connection is not null)
                        {
                            bool abortTunnelConnectionDueToError = false;

                            if (coreMessage.Length >= 1 &&
                                coreMessage.Span[0] is ProxyMessageTypeData)
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
                                catch (ArgumentException)
                                {
                                    // An argument exception means the passed data length or
                                    // window size would exceed the allowed size, which means
                                    // the partner proxy doesn't work correctly or might be
                                    // malicious. In that case, we abort the tunnel connection
                                    // (not the whole connection to the gateway, because when
                                    // we are the proxy-client that can possibly handle
                                    // multiple connections, this would also affect connections
                                    // from other proxy-servers).
                                    abortTunnelConnectionDueToError = true;
                                }
                            }
                            else if (coreMessage.Length >= 1 + sizeof(int) &&
                                coreMessage.Span[0] is ProxyMessageTypeUpdateWindow)
                            {
                                // Update the receive window size.
                                int windowSize = BinaryPrimitives.ReadInt32BigEndian(
                                    coreMessage.Span[1..]);

                                try
                                {
                                    connection.UpdateReceiveWindow(windowSize);
                                }
                                catch (ArgumentException)
                                {
                                    // See comments above.
                                    abortTunnelConnectionDueToError = true;
                                }
                            }

                            if (abortTunnelConnectionDueToError ||
                                coreMessage.Length >= 1 &&
                                coreMessage.Span[0] is ProxyMessageTypeAbortConnection)
                            {
                                // Abort the connection, which will reset it immediately. We
                                // use StopAsync() to ensure the connection is removed from
                                // the list of active connections before we continue.
                                if (!abortTunnelConnectionDueToError)
                                {
                                    // Because the abort has been initiated by the partner
                                    // proxy, we try to suppress sending the abort message
                                    // (as the partner already knows about the abort).
                                    connection.Data.SuppressSendAbortMessage = true;
                                    Thread.MemoryBarrier();
                                }

                                await connection.StopAsync();
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

            // Once this method returns, the endpoint may no longer
            // be used to send data, so clear the instance.
            lock (this.syncRoot)
            {
                this.tcpEndpoint = null;
            }
        }
    }

    private void StartTcpTunnelConnection(
        TcpClientFramingEndpoint endpoint,
        long partnerProxyId,
        Dictionary<long, ProxyTunnelConnection<TunnelConnectionData>> activeConnections,
        long connectionId,
        TcpClient remoteClient,
        Func<CancellationToken, ValueTask>? connectHandler = null)
    {
        Debug.Assert(Monitor.IsEntered(this.syncRoot));

        var connection = default(ProxyTunnelConnection<TunnelConnectionData>);
        connection = new(
            remoteClient,
            connectHandler,
            receiveHandler: receiveBuffer =>
            {
                // Forward the data packet.
                int length = sizeof(long) + 1 + receiveBuffer.Length;

                var (message, coreMessage) = PreparePartnerProxyMessage(
                    length,
                    this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                BinaryPrimitives.WriteInt64BigEndian(coreMessage.Span, connectionId);
                int pos = sizeof(long);

                coreMessage.Span[pos++] = ProxyMessageTypeData;
                receiveBuffer.CopyTo(coreMessage[pos..]);
                pos += receiveBuffer.Length;

                endpoint.SendMessageByQueue(message);
            },
            transmitWindowUpdateHandler: window =>
            {
                // Forward the transmit window update.
                int length = sizeof(long) + 1 + sizeof(int);

                var (message, coreMessage) = PreparePartnerProxyMessage(
                    length,
                    this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                BinaryPrimitives.WriteInt64BigEndian(coreMessage.Span, connectionId);
                int pos = sizeof(long);

                coreMessage.Span[pos++] = ProxyMessageTypeUpdateWindow;
                BinaryPrimitives.WriteInt32BigEndian(coreMessage.Span[pos..], window);
                pos += sizeof(int);

                // Send the window update with a high priority, to ensure it will be sent
                // before data packets if the queue already contains entries. This helps
                // to avoid a slowdown of the connection if both sides currently send data.
                // 
                // From the protocol side this will work because even if we are the
                // proxy-server which accepts TCP connections (and has to inform the
                // proxy-client about the new connection), we can only send a window
                // update if the proxy-client has already received the "open connection"
                // message (as this is a prerequisite before the proxy-client can send us data
                // for the connection that might trigger a window update from our side),
                // so a window update message can never be sent to the partner before
                // sending the "open connection" message, even if it is sent with a higher
                // priority than that message.
                // 
                // Additionally, even though a window update message would be sent with a
                // higher priority than a session iteration acknowledge message (after receiving
                // a new session status), this will not cause problems as only the proxy-server
                // needs to acknowledge a new session iteration, and it's not possible for the
                // proxy-server to send window update messages before sending the acknowledge
                // message as that is a prerequisite before the proxy-client will receive a
                // forwarded connection and can send data to the proxy-server, which would then
                // cause a window update message to be sent.
                endpoint.SendMessageByQueue(message, highPriority: true);
            },
            connectionFinishedHandler: isAbort =>
            {
                Thread.MemoryBarrier();

                if (isAbort && !connection!.Data.SuppressSendAbortMessage)
                {
                    // Notify the partner that the connection was aborted.
                    // This must be done before removing the connection from the
                    // `activeConnections` dictionary; see comment below.
                    int length = sizeof(long) + 1;

                    var (message, coreMessage) = PreparePartnerProxyMessage(
                        length,
                        this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                    BinaryPrimitives.WriteInt64BigEndian(coreMessage.Span, connectionId);
                    int pos = sizeof(long);

                    coreMessage.Span[pos++] = ProxyMessageTypeAbortConnection;

                    endpoint.SendMessageByQueue(message);
                }

                lock (this.syncRoot)
                {
                    // The connection is finished, so remove it. After removing it, we mustn't
                    // call the `Endpoint.SendMessageByQueue()` method again, because the endpoint
                    // may already have stopped (and so calling that method would throw).
                    // Note that the receiveTask is still running (we are being called from it,
                    // so we can't wait for it by calling `StopAsync()`), but at this stage the
                    // connection is considered to be finished and it doesn't have any more
                    // resources open, so it is OK to just remove it.
                    activeConnections.Remove(connectionId);
                }
            });

        // Ensure other threads can see the connection value after starting the connection.
        Thread.MemoryBarrier();

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
                new Dictionary<long, ProxyTunnelConnection<TunnelConnectionData>>()))
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
            var connectionsToWait = new List<ProxyTunnelConnection<TunnelConnectionData>>();

            lock (this.syncRoot)
            {
                // After we leave the lock, the listener won't add any more
                // connections to the list until we call HandlePartnerProxyAvailable
                // again.
                this.activePartnerProxiesAndConnections.Remove(partnerProxyId);
                connectionsToWait.AddRange(activeConnections.Values);
            }

            foreach (var pair in connectionsToWait)
            {
                // Sending the abort message won't have any effect (as the partner has
                // become unavailable and wouldn't receive it), so we try to suppress it.
                pair.Data.SuppressSendAbortMessage = true;
                Thread.MemoryBarrier();

                await pair.StopAsync();
            }
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

        // Additionally, use a smaller socket send buffer size to reduce
        // buffer bloat.
        client.Client.SendBufferSize = Constants.SocketSendBufferSize;

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
                catch (Exception ex) when (ex.CanCatch())
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
            coreMessage.Span[sizeof(long)] = ProxyMessageTypeOpenConnection;

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
