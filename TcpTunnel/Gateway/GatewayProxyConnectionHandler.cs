using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using TcpTunnel.Networking;
using TcpTunnel.Utils;

namespace TcpTunnel.Gateway;

/*
 * Architecture:
 * - Gateway: Listens on the specified port for incoming proxy connections that authenticate for a session ID.
 *   Only one proxy-client can connect to the session, but multiple proxy-servers can connect to the session.
 *   The main functionality is to forward packets between partner proxies (proxy-client to a proxy-server or vice versa).
 *   
 * - Proxy-Client: Waits for connection requests from one of the partner proxy-servers, and then creates outgoing TCP connections.
 * - Proxy-Server: Listens on the specified IP/Port combinations for incoming TCP connections, and then sends the connection request to the partner proxy-client.
 * 
 * PROTOCOL (Frame Payload) GATEWAY COMMUNICATION:
 * - 0x00: (Proxy to Gateway) Authentication + Proxy-Type (0x01 for proxy-server) + Login-Prerequisite-String + Session-ID (int) + Session-Password
 * - 0x01: (Gateway to Proxy) Authentication Result.
 *     - 0x00: Auth failed.
 *     - 0x01: Auth successful.
 * - 0x02: (Gateway to Proxy) Session Status (sent after successful authentication if partner proxies are available, and during runtime if status changes):
 *     + [if proxy is proxy-client) Partner Proxy ID (Int64)
 *     + 0x01: Partner proxy is available (in case of proxy-server, this means the proxy needs to acknowledge the new session iteration) or 0x00: Partner proxy is unavailable.
 * - 0x03 (Proxy to Gateway): [if proxy is proxy-server] Acknowledge new session iteration after being informed that the partner proxy is now available.
 * - 0x20 (both directions): Proxy-to-Proxy communication. The gateway forwards the packet to the partner proxy if available [and, in case of proxy-server, if current session iteration has been acknowledged].
 *     + [if sending/receiving proxy is proxy-client] Partner Proxy ID (Int64)
 *     + Further payload is defined by PROXY-TO-PROXY COMMUNICATION.
 * 
 * - 0xFF: Reserved for Ping.
 */
internal class GatewayProxyConnectionHandler
{
    private readonly Gateway gateway;
    private readonly TcpClientFramingConnection proxyConnection;
    private readonly EndPoint clientEndpoint;

    private long proxyId;
    private Session? authenticatedSession;

    private int sessionIterationsToAcknowledge;

    public GatewayProxyConnectionHandler(
        Gateway gateway,
        TcpClientFramingConnection proxyConnection,
        EndPoint clientEndpoint)
    {
        this.gateway = gateway;
        this.proxyConnection = proxyConnection;
        this.clientEndpoint = clientEndpoint;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                while (true)
                {
                    var packet = await this.proxyConnection.ReceiveMessageAsync(
                        Gateway.MaxReceiveMessageSize,
                        cancellationToken);

                    if (packet is null)
                        return;

                    this.proxyConnection.HandlePing();
                    var packetBuffer = packet.Value.Buffer;

                    bool sessionWasAuthenticated = false;
                    lock (this.gateway.SyncRoot)
                    {
                        // Need to access the field within the lock, as it might be changed by
                        // a different connection handler.
                        if (this.authenticatedSession is { } session)
                        {
                            sessionWasAuthenticated = true;
                            bool isProxyClient = this.proxyId is Constants.ProxyClientId;

                            if (packetBuffer.Length >= 1 && packetBuffer.Span[0] is 0x03 && !isProxyClient)
                            {
                                // Acknowledge the current session iteration.
                                if (this.sessionIterationsToAcknowledge <= 0)
                                    throw new InvalidOperationException(); // Invalid message

                                this.sessionIterationsToAcknowledge--;
                            }
                            else if (packetBuffer.Length >= 1 + (isProxyClient ? sizeof(long) : 0) &&
                                packetBuffer.Span[0] is Constants.TypeProxyToProxyCommunication)
                            {
                                // Proxy-to-proxy communication.
                                // Forward the packet to the partner proxy, if available and if
                                // the current proxy has acknowledged the current session iteration.
                                if (isProxyClient)
                                {
                                    long partnerProxyId = BinaryPrimitives.ReadInt64BigEndian(packetBuffer.Span[1..]);
                                    if (partnerProxyId is Constants.ProxyClientId)
                                        throw new InvalidDataException();

                                    if (session.Proxies.TryGetValue(partnerProxyId, out var partnerProxy))
                                    {
                                        // Strip out the proxy ID.
                                        var targetPacket = new byte[packetBuffer.Length - sizeof(long)];
                                        targetPacket[0] = Constants.TypeProxyToProxyCommunication;

                                        // Need to copy the data because it will be queued (and the buffer
                                        // may be reused by the caller).
                                        packetBuffer[(1 + sizeof(long))..].CopyTo(targetPacket.AsMemory()[1..]);

                                        partnerProxy.proxyConnection.SendMessageByQueue(targetPacket);
                                    }
                                }
                                else if (this.sessionIterationsToAcknowledge is 0 &&
                                    session.Proxies.TryGetValue(Constants.ProxyClientId, out var partnerProxy))
                                {
                                    // Add the sender proxy ID.
                                    var targetPacket = new byte[packetBuffer.Length + sizeof(long)];
                                    targetPacket[0] = Constants.TypeProxyToProxyCommunication;
                                    BinaryPrimitives.WriteInt64BigEndian(targetPacket.AsSpan()[1..], this.proxyId);

                                    // See comment above.
                                    packetBuffer[1..].CopyTo(targetPacket.AsMemory()[(1 + sizeof(long))..]);

                                    partnerProxy.proxyConnection.SendMessageByQueue(targetPacket);
                                }
                            }
                        }
                    }

                    if (!sessionWasAuthenticated)
                    {
                        // Only allow authentication packets. Because we might need to do a
                        // (possibly expensive) authentication, we run this code outside of the
                        // lock, until we could actually authenticate the proxy.
                        if (packetBuffer.Length >= 2 + Constants.loginPrerequisiteBytes.Length + sizeof(int) &&
                            packetBuffer.Span[0] is 0x00)
                        {
                            // Authentication.
                            // The proxy-client will always have ID 0.
                            bool isProxyClient = packetBuffer.Span[1] is 0x00;
                            var clientPrerequisite = packetBuffer[2..][..Constants.loginPrerequisiteBytes.Length];

                            bool couldAuthenticate = false;
                            if (clientPrerequisite.Span.SequenceEqual(Constants.loginPrerequisiteBytes.Span))
                            {
                                int sessionId = BinaryPrimitives.ReadInt32BigEndian(
                                    packetBuffer.Span[(2 + Constants.loginPrerequisiteBytes.Length)..]);
                                var enteredPasswordBytes = packetBuffer
                                    [(2 + Constants.loginPrerequisiteBytes.Length + sizeof(int))..];

                                // Check if the session exists.
                                if (this.gateway.Sessions.TryGetValue(sessionId, out var session))
                                {
                                    // Verify the session password. For this, we need to
                                    // use FixedTimeEquals to prevent timing attacks.
                                    var correctPasswordBytes = isProxyClient ?
                                        session.ProxyClientPasswordBytes :
                                        session.ProxyServerPasswordBytes;

                                    if (CryptographicOperations.FixedTimeEquals(
                                        enteredPasswordBytes.Span,
                                        correctPasswordBytes.Span))
                                    {
                                        // Proxy authenticated successfully for the given
                                        // session ID.
                                        couldAuthenticate = true;

                                        this.gateway.Logger?.Invoke(
                                            $"Proxy '{this.clientEndpoint}' authenticated for Session ID '{sessionId}' " +
                                            $"({(isProxyClient ? "proxy-client" : "proxy-server")}).");

                                        // Enter the lock again. We don't need to check whether
                                        // authenticatedSession is still null, as it can only be
                                        // set to null by another connection handler, but not to
                                        // an non-null value.
                                        lock (this.gateway.SyncRoot)
                                        {
                                            this.authenticatedSession = session;
                                            this.proxyId = isProxyClient ?
                                                Constants.ProxyClientId :
                                                checked(session.NextProxyId++);

                                            Debug.Assert(
                                                isProxyClient || this.proxyId is not Constants.ProxyClientId);

                                            // Notify the proxy that the authentication succeeded.
                                            var response = new byte[] { 0x01, 0x01 };
                                            this.proxyConnection.SendMessageByQueue(response);

                                            // Check if an old proxy-client is present.
                                            // TODO: What should happen with that connection?
                                            // Currently we just de-authenticate it without
                                            // informing the corresponding proxy.
                                            if (isProxyClient && session.Proxies.TryGetValue(
                                                Constants.ProxyClientId,
                                                out var proxyClient))
                                            {
                                                proxyClient.authenticatedSession = null;
                                            }

                                            // Set the new proxy.
                                            session.Proxies[this.proxyId] = this;

                                            // Inform the partnered proxies about the new
                                            // availability.
                                            this.SendSessionStatuses(true);
                                        }
                                    }
                                }

                                if (!couldAuthenticate)
                                {
                                    // Notify the proxy that the authentication failed.
                                    var response = new byte[] { 0x01, 0x00 };
                                    this.proxyConnection.SendMessageByQueue(response);

                                    // Also, close the connection.
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                this.HandleClose();
            }
        }
        catch (Exception ex) when (ex.CanCatch() && false)
        {
            // We need a separate exception filter to prevent the finally handler
            // from being called in case of an OOME.
            throw;
        }
    }

    private void HandleClose()
    {
        lock (this.gateway.SyncRoot)
        {
            if (this.authenticatedSession is { } session)
            {
                this.authenticatedSession.Proxies.Remove(this.proxyId);

                // Inform the partnered proxies about the new
                // availability.
                this.SendSessionStatuses(false);

                this.authenticatedSession = null;
            }
        }
    }

    private void SendSessionStatuses(bool isAdded)
    {
        Debug.Assert(Monitor.IsEntered(this.gateway.SyncRoot));

        bool isProxyClient = this.proxyId is Constants.ProxyClientId;
        if (isProxyClient)
        {
            foreach (var proxyServer in this.authenticatedSession!.Proxies)
            {
                if (proxyServer.Key is Constants.ProxyClientId)
                    continue;

                proxyServer.Value.SendSessionStatus(null, isAdded);

                if (isAdded)
                {
                    this.SendSessionStatus(proxyServer.Key, true);

                    // Increment the session iteration which the proxy-servers need
                    // to acknowledge.
                    // Requiring to acknowledge a new session iteration ensures
                    // that we don't forward "old" messages from a proxy-server that
                    // were intended for a previously connected proxy-client to the
                    // newly connected proxy-client. For the other direction, this isn't
                    // necessary as the gateway will assign incrementing IDs
                    // to the connected proxy-server instances.
                    proxyServer.Value.sessionIterationsToAcknowledge++;
                }
            }
        }
        else
        {
            this.authenticatedSession!.Proxies.TryGetValue(
                Constants.ProxyClientId,
                out var proxyClient);

            if (proxyClient is not null)
            {
                proxyClient.SendSessionStatus(this.proxyId, isAdded);

                if (isAdded)
                {
                    this.SendSessionStatus(null, true);

                    // Increment the session iteration which the proxy-server needs
                    // to acknowledge. While this technically wouldn't be needed (this is
                    // a new connection from a proxy-server so it can't have existing
                    // messages for a previous proxy-client in its send queue), this
                    // simplifies the handling because we don't need to differentiate
                    // between the initial and later session status messages.
                    this.sessionIterationsToAcknowledge++;
                }
            }
        }
    }

    private void SendSessionStatus(long? partnerClientId, bool isAvailable)
    {
        bool isProxyClient = this.proxyId is Constants.ProxyClientId;
        if (isProxyClient != partnerClientId is not null)
            throw new ArgumentException();

        var response = new byte[isProxyClient ? (2 + 8) : 2];

        int pos = 0;
        response[pos++] = 0x02;

        if (isProxyClient)
        {
            BinaryPrimitives.WriteInt64BigEndian(response.AsSpan()[pos..], partnerClientId!.Value);
            pos += sizeof(long);
        }

        response[pos++] = isAvailable ? (byte)0x01 : (byte)0x00;

        this.proxyConnection.SendMessageByQueue(response);
    }
}
