using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SimpleSocketClient;

using TcpTunnel.Networking;
using TcpTunnel.Utils;

namespace TcpTunnel.Proxy;

/**
 * PROXY-TO-PROXY COMMUNICATION:
 * - 8 bytes ConnectionID + 0x00 + 4 bytes port + utf-8 string Host: Open a new connection.
 * - 8 bytes ConnectionID + 0x01 + arbitrary bytes: Transmit data packet or shutdown the
 *   connection if arbitrary bytes's length is 0.
 * - 8 bytes ConnectionID + 0x02: Abort the connection.
 * - 8 bytes ConnectionID + 0x03 + 4 bytes window size: Update receive window.
 */
public partial class Proxy : IInstance
{
    // See comments in `Gateway.MaxReceiveMessageSize`. 
    // We use a size that's a bit larger than the gateway's `MaxReceiveMessageSize` size, to
    // compensate for the overhead when the gateway forwards a proxy message (which didn't
    // exceed the gateway's `MaxReceiveMessageSize` when the gateway received it, but would
    // exceed that size when the gateway forwards the message to the partner proxy, e.g.
    // through the addition of the partner proxy ID).
    // This is to ensure we won't throw in such a case, to avoid aborting the connection to
    // the gateway if we are the proxy-client, as that would also affect all other connected
    // proxy-servers.
    public const int MaxReceiveMessageSize = Gateway.Gateway.MaxReceiveMessageSize + 256;

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
    private TcpFramingConnection? gatewayConnection;

    private ProxyServerListener? listener;

    /// <summary>
    /// The dictionary of active connections.
    /// </summary>
    private readonly Dictionary<ulong /* proxyId */,
        Dictionary<ulong /* connectionId */, ProxyTunnelConnection<TunnelConnectionData>>> activePartnerProxiesAndConnections =
        [];

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
        ulong? remoteProxyId)
    {
        var message = new byte[1 + (remoteProxyId is not null ? sizeof(ulong) : 0) + length];

        int pos = 0;
        message[pos++] = Constants.TypeProxyToProxyCommunication;

        if (remoteProxyId is not null)
        {
            BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan()[pos..], remoteProxyId.Value);
            pos += sizeof(ulong);
        }

        var coreMessage = message.AsMemory()[pos..][..length];
        return (message, coreMessage);
    }

    private static bool TryDecodePartnerProxyMessage(
        Memory<byte> message,
        bool containsPartnerProxyId,
        out Memory<byte> coreMessage,
        out ulong? partnerProxyId)
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
            if (coreMessage.Length < sizeof(ulong))
                throw new InvalidDataException();

            partnerProxyId = BinaryPrimitives.ReadUInt64BigEndian(coreMessage.Span);
            coreMessage = coreMessage[sizeof(ulong)..];
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
                this.AcceptProxyServerSocket);

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
        this.logger?.Invoke($"Connecting to gateway (Using SSL: {this.gatewayUseSsl})...");

        string? lastConnectionError = null;

        try
        {
            while (true)
            {
                readTaskCancellationToken.ThrowIfCancellationRequested();

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

                bool wasConnected = false;
                var caughtException = default(Exception);
                try
                {
                    var gatewayConnection = new TcpFramingConnection(
                        socket,
                        useSendQueue: true,
                        usePingTimer: false,
                        connectHandler: async cancellationToken =>
                        {
                            // Note: We will log the "Connected" message in the stream modifier
                            // callback, because only after that the connection can considered
                            // to be fully established.
                            await socket.ConnectAsync(
                                this.gatewayHost,
                                this.gatewayPort,
                                cancellationToken);

                            // After the socket is connected, configure it to disable the Nagle
                            // algorithm, disable delayed ACKs, and enable TCP keep-alive.
                            SocketConfigurator.ConfigureSocket(socket, enableKeepAlive: true);

                            // Use a smaller socket send buffer size to reduce buffer bloat.
                            socket.SendBufferSize = Constants.SocketSendBufferSize;
                        },
                        streamModifier: async (networkStream, cancellationToken) =>
                        {
                            var modifiedStream = default(Stream);

                            var sslNegotiatedProtocol = default(SslProtocols);
                            var sslNegotiatedCipherSuite = default(TlsCipherSuite);

                            if (this.gatewayUseSsl)
                            {
                                var sslStream = new SslStream(networkStream);
                                try
                                {
                                    await sslStream.AuthenticateAsClientAsync(
                                        new SslClientAuthenticationOptions()
                                        {
                                            TargetHost = this.gatewayHost
                                        },
                                        cancellationToken);

                                    sslNegotiatedProtocol = sslStream.SslProtocol;
                                    sslNegotiatedCipherSuite = sslStream.NegotiatedCipherSuite;
                                }
                                catch (Exception ex) when (ex.CanCatch())
                                {
                                    await sslStream.DisposeAsync();
                                    throw;
                                }

                                modifiedStream = sslStream;
                            }

                            wasConnected = true;

                            if (this.logger is not null)
                            {
                                string sslDetails = $"Using SSL: {this.gatewayUseSsl}";

                                if (this.gatewayUseSsl)
                                {
                                    sslDetails +=
                                        $", Protocol: {Gateway.Gateway.FormatSslProtocol(sslNegotiatedProtocol)}, " +
                                        $"Cipher Suite: {sslNegotiatedCipherSuite}";
                                }

                                this.logger.Invoke(
                                    $"Connection established to gateway " +
                                    $"'{this.gatewayHost}:{this.gatewayPort.ToString(CultureInfo.InvariantCulture)}' ({sslDetails}). " +
                                    $"Authenticating for Session ID '{this.sessionId.ToString(CultureInfo.InvariantCulture)}' " +
                                    $"({(this.proxyServerConnectionDescriptors is not null ? "proxy-server" : "proxy-client")})...");
                            }

                            return modifiedStream;
                        });

                    await gatewayConnection.RunConnectionAsync(
                        cancellationToken => this.RunConnectionAsync(gatewayConnection, cancellationToken),
                        readTaskCancellationToken);
                }
                catch (Exception ex) when (ex.CanCatch())
                {
                    // Ignore, and try again. This includes the case when an
                    // OperationCanceledException was thrown above, which might be caused by
                    // the Connection instance in order to cancel the current connection.
                    // We check later if it was caused by us canceling the CTS.
                    caughtException = ex;
                }
                finally
                {
                    try
                    {
                        socket.Dispose();
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
        TcpFramingConnection gatewayConnection)
    {
        while (true)
        {
            bool exit = await pingTimerSemaphore.WaitAsync(30000);
            if (exit)
                return;

            gatewayConnection.SendMessageByQueue(new byte[] { 0xFF });
        }
    }

    private async Task RunConnectionAsync(
        TcpFramingConnection gatewayConnection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var pingTimerSemaphore = new SemaphoreSlim(0);
            var pingTimerTask = default(Task);

            bool isProxyClient = this.proxyServerConnectionDescriptors is null;

            // Beginning from this stage, the connection can be used, so we
            // can now set it.
            lock (this.syncRoot)
            {
                this.gatewayConnection = gatewayConnection;
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

                gatewayConnection.SendMessageByQueue(loginString);

                // Start the ping timer task, then receive packets.
                pingTimerTask = ExceptionUtils.StartTask(
                    () => this.RunPingTaskAsync(pingTimerSemaphore, gatewayConnection));

                while (true)
                {
                    var packet = await gatewayConnection.ReceiveMessageAsync(
                        MaxReceiveMessageSize,
                        cancellationToken);

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
                    else if (packetBuffer.Length >= 2 + (isProxyClient ? sizeof(ulong) : 0) &&
                        packetBuffer.Span[0] is 0x02)
                    {
                        // New Session Status.
                        packetBuffer = packetBuffer[1..];

                        ulong? partnerProxyId = null;
                        if (isProxyClient)
                        {
                            partnerProxyId = BinaryPrimitives.ReadUInt64BigEndian(packetBuffer.Span);
                            packetBuffer = packetBuffer[sizeof(ulong)..];

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
                            this.HandlePartnerProxyAvailable(
                                gatewayConnection,
                                partnerProxyId ?? Constants.ProxyClientId);
                    }
                    else if (TryDecodePartnerProxyMessage(
                        packetBuffer,
                        isProxyClient,
                        out var coreMessage,
                        out ulong? partnerProxyIdNullable))
                    {
                        // Proxy to proxy communication.
                        if (partnerProxyIdNullable is { } value && value is Constants.ProxyClientId)
                            throw new InvalidDataException();

                        ulong partnerProxyId = partnerProxyIdNullable ?? Constants.ProxyClientId;

                        // We don't need a lock to access the dictionary here since it is
                        // only modified by us (the receiver task). We only need a lock to
                        // change it, and when reading it from another task.
                        if (!this.activePartnerProxiesAndConnections.TryGetValue(
                            partnerProxyId,
                            out var activeConnections))
                            throw new InvalidDataException();

                        // If we receive a partner proxy message (represented by `coreMessage`)
                        // that violates the protocol (which would mean the partner proxy would
                        // be buggy or malicious), instead of throwing (which would abort the
                        // connection to the gateway), we ignore the message or try to respond
                        // with an error. Otherwise, if we would throw and we are the
                        // proxy-client, we would also abort all connections for all other
                        // proxy-servers that actually behave correctly.
                        if (coreMessage.Length < sizeof(ulong))
                            continue;

                        ulong connectionId = BinaryPrimitives.ReadUInt64BigEndian(coreMessage.Span);
                        coreMessage = coreMessage[sizeof(ulong)..];

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
                            // to check whether the target endpoint is allowed. Otherwise, we
                            // respond to the partner proxy that the connection was aborted.
                            // We also do this if the partner proxy server incorrectly uses a
                            // connection ID that's already in use (for the reasons mentioned
                            // above).
                            bool sendAbort = false;
                            ProxyTunnelConnection<TunnelConnectionData>? existingConnection;

                            lock (this.syncRoot)
                            {
                                activeConnections.TryGetValue(connectionId, out existingConnection);

                                if (existingConnection is null &&
                                    (this.proxyClientAllowedTargetEndpoints is null ||
                                    this.proxyClientAllowedTargetEndpoints.Contains((hostname, port))))
                                {
                                    var remoteSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);

                                    this.StartTcpTunnelConnection(
                                        gatewayConnection,
                                        partnerProxyId,
                                        activeConnections,
                                        connectionId,
                                        remoteSocket,
                                        async cancellationToken =>
                                        {
                                            await remoteSocket.ConnectAsync(hostname, port, cancellationToken);

                                            // After the socket is connected, configure it to disable the Nagle
                                            // algorithm, disable delayed ACKs, and enable TCP keep-alive.
                                            SocketConfigurator.ConfigureSocket(
                                                remoteSocket,
                                                enableKeepAlive: true);

                                            // Additionally, use a smaller socket send buffer size to reduce
                                            // buffer bloat.
                                            remoteSocket.SendBufferSize = Constants.SocketSendBufferSize;
                                        });
                                }
                                else
                                {
                                    sendAbort = true;
                                }
                            }

                            if (sendAbort)
                            {
                                if (existingConnection is not null)
                                {
                                    // Abort the already existing connection. We suppress sending
                                    // the abort message here, because afterwards we want to send a
                                    // separate abort message in any case (the existing connection
                                    // might already be in the stage of being closed and therefore
                                    // might not always send an abort message afterwards).
                                    // However, note that this is just kind of a help for debugging
                                    // the partner proxy, because the protocol has already been
                                    // violated.
                                    existingConnection.Data.SuppressSendAbortMessage = true;
                                    Thread.MemoryBarrier();

                                    await existingConnection.StopAsync();
                                }

                                // Notify the partner that the connection had to be aborted.
                                int length = sizeof(ulong) + 1;

                                var (messageToSend, coreMessageToSend) = PreparePartnerProxyMessage(
                                    length,
                                    partnerProxyId);

                                BinaryPrimitives.WriteUInt64BigEndian(coreMessageToSend.Span, connectionId);
                                int pos = sizeof(ulong);

                                coreMessageToSend.Span[pos++] = ProxyMessageTypeAbortConnection;

                                gatewayConnection.SendMessageByQueue(messageToSend);
                            }
                        }
                        else
                        {
                            ProxyTunnelConnection<TunnelConnectionData>? connection;
                            lock (this.syncRoot)
                            {
                                // We might fail to find the connectionId if the connection
                                // was already fully closed or aborted.
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
                                        // (for the reasons mentioned above).
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
                foreach (ulong partnerProxyId in this.activePartnerProxiesAndConnections.Keys.ToArray())
                    await this.HandlePartnerProxyUnavailableAsync(partnerProxyId);

                pingTimerSemaphore.Release();
                await (pingTimerTask ?? Task.CompletedTask);

                // Once this method returns, the connection may no longer
                // be used to send data, so clear the instance.
                lock (this.syncRoot)
                {
                    this.gatewayConnection = null;
                }
            }
        }
        catch (Exception ex) when (ex.CanCatch() && false)
        {
            // We need a separate exception filter to prevent the finally handlers
            // from being called in case of an OOME.
            // This is a separate try-catch block to include the case when e.g. the above
            // `finally` block throws an OOME (and then we wouldn't wait for the ping
            // task to finish), in which case we would then dispose the ping task semaphore
            // before the exception filter of the outer async method would terminate
            // the app.
            throw;
        }
    }

    private void StartTcpTunnelConnection(
        TcpFramingConnection gatewayConnection,
        ulong partnerProxyId,
        Dictionary<ulong, ProxyTunnelConnection<TunnelConnectionData>> activeConnections,
        ulong connectionId,
        Socket remoteSocket,
        Func<CancellationToken, ValueTask>? connectHandler = null)
    {
        Debug.Assert(Monitor.IsEntered(this.syncRoot));

        var connection = default(ProxyTunnelConnection<TunnelConnectionData>);
        connection = new(
            remoteSocket,
            connectHandler,
            receiveHandler: receiveBuffer =>
            {
                // Forward the data packet.
                int length = sizeof(ulong) + 1 + receiveBuffer.Length;

                var (message, coreMessage) = PreparePartnerProxyMessage(
                    length,
                    this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                BinaryPrimitives.WriteUInt64BigEndian(coreMessage.Span, connectionId);
                int pos = sizeof(ulong);

                coreMessage.Span[pos++] = ProxyMessageTypeData;
                receiveBuffer.CopyTo(coreMessage[pos..]);
                pos += receiveBuffer.Length;

                gatewayConnection.SendMessageByQueue(message);
            },
            transmitWindowUpdateHandler: window =>
            {
                // Forward the transmit window update.
                int length = sizeof(ulong) + 1 + sizeof(int);

                var (message, coreMessage) = PreparePartnerProxyMessage(
                    length,
                    this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                BinaryPrimitives.WriteUInt64BigEndian(coreMessage.Span, connectionId);
                int pos = sizeof(ulong);

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
                gatewayConnection.SendMessageByQueue(message, highPriority: true);
            },
            connectionFinishedHandler: isAbort =>
            {
                Thread.MemoryBarrier();

                if (isAbort && !connection!.Data.SuppressSendAbortMessage)
                {
                    // Notify the partner that the connection was aborted.
                    // This must be done before removing the connection from the
                    // `activeConnections` dictionary; see comment below.
                    int length = sizeof(ulong) + 1;

                    var (message, coreMessage) = PreparePartnerProxyMessage(
                        length,
                        this.proxyServerConnectionDescriptors is null ? partnerProxyId : null);

                    BinaryPrimitives.WriteUInt64BigEndian(coreMessage.Span, connectionId);
                    int pos = sizeof(ulong);

                    coreMessage.Span[pos++] = ProxyMessageTypeAbortConnection;

                    gatewayConnection.SendMessageByQueue(message);
                }

                lock (this.syncRoot)
                {
                    // The connection is finished, so remove it. After removing it, we mustn't
                    // call the `Connection.SendMessageByQueue()` method again, because the
                    // connection may already have stopped (and so calling that method would
                    // throw).
                    // We might still receive messages for the connection after removing it
                    // (which the partner proxy has sent before it knew that the connection
                    // has been aborted), which is OK since we will ignore them (as we won't
                    // find the connection ID in the dictionary).
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
        try
        {
            activeConnections.Add(connectionId, connection);
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            // There are too many entries in the dictionary
            // (should never happen in practice).
            Environment.FailFast(ex.Message, ex);
            throw; // Satisfy CFA
        }

        connection.Start();
    }

    private void HandlePartnerProxyAvailable(TcpFramingConnection gatewayConnection, ulong partnerProxyId)
    {
        lock (this.syncRoot)
        {
            // If there were too many entries in the dictionary (which should never
            // happen in practice), this would throw and thus aborting the
            // connection to the gateway.
            if (!this.activePartnerProxiesAndConnections.TryAdd(
                partnerProxyId,
                []))
                throw new InvalidDataException();

            if (this.proxyServerConnectionDescriptors is not null)
            {
                // Acknowledge the new session iteration. Sending this message must be
                // done within the lock, as otherwise we might already start to send
                // create connection messages (through the listener) which the tunnel
                // gateway would then ignore.
                var response = new byte[] { 0x03 };
                gatewayConnection.SendMessageByQueue(response);
            }
        }
    }

    private async ValueTask HandlePartnerProxyUnavailableAsync(ulong partnerProxyId)
    {
        if (this.activePartnerProxiesAndConnections.TryGetValue(
            partnerProxyId,
            out var activeConnections))
        {
            // Close the connections.
            // We need to copy the list since we wait for the connections to exit,
            // and they might want to remove themselves from the list in the
            // meanwhile.
            ProxyTunnelConnection<TunnelConnectionData>[] connectionsToWait;

            lock (this.syncRoot)
            {
                // After we leave the lock, the listener won't add any more
                // connections to the list until we call HandlePartnerProxyAvailable
                // again.
                this.activePartnerProxiesAndConnections.Remove(partnerProxyId);
                connectionsToWait = activeConnections.Values.ToArray();
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

    private void AcceptProxyServerSocket(
        ulong connectionId,
        Socket socket,
        ProxyServerConnectionDescriptor descriptor)
    {
        // After the socket is connected, configure it to disable the Nagle
        // algorithm, disable delayed ACKs, and enable TCP keep-alive.
        SocketConfigurator.ConfigureSocket(socket, enableKeepAlive: true);

        // Additionally, use a smaller socket send buffer size to reduce
        // buffer bloat.
        socket.SendBufferSize = Constants.SocketSendBufferSize;

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
                    socket.Close(0);
                    socket.Dispose();
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
            int responseLength = sizeof(ulong) + 1 + sizeof(int) + hostnameBytes.Length;

            var (message, coreMessage) = PreparePartnerProxyMessage(responseLength, null);

            BinaryPrimitives.WriteUInt64BigEndian(coreMessage.Span, connectionId);
            coreMessage.Span[sizeof(ulong)] = ProxyMessageTypeOpenConnection;

            BinaryPrimitives.WriteInt32BigEndian(
                coreMessage.Span[(sizeof(ulong) + 1)..],
                descriptor.RemotePort);

            hostnameBytes.CopyTo(coreMessage.Span[(sizeof(ulong) + 1 + sizeof(int))..]);

            // Send the message. From that point, our receiver task might already receive
            // events for the connection, which then need to wait for the lock.
            this.gatewayConnection!.SendMessageByQueue(message);

            // Add the connection to the list and start it.
            this.StartTcpTunnelConnection(
                this.gatewayConnection!,
                Constants.ProxyClientId,
                activeConnections,
                connectionId,
                socket);
        }
    }
}
