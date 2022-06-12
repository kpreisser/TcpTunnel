using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

using TcpTunnel.Networking;

using SystemNetEndpoint = System.Net.EndPoint;

namespace TcpTunnel.Server;

/*
 * PROTOCOL (Frame Payload) SERVER COMMUNICATION:
 * 0x00: (Client to Server) Authentication + Client-Type (0x00 or 0x01) + Login-Prerequisite-String + Session-ID (int) + Session-Password
 * 0x01: (Server to Client) Authentication failed, try again.
 * 0x02: (Server to Client) Session Status (sent after successfull authentication and during runtime if status changes):
 *     -> 0x00: Partner client is available (this means the client needs to acknowledge the new session iteration).
 *     -> 0x01: Partner client is unavailable.
 * 0x03 (Client to Server): Acknowledge new session iteration after being informed that the partner client is now available.
 * 0x20 (both directions): Client-to-Client communication. The server forwards the packet to the partner client if available and current session iteration has been acknowledged.
 *     -> Further payload is defined by CLIENT-TO-CLIENT COMMUNICATION.
 * 
 * 0xFF: Reserved for Ping.
 */
internal class ServerConnectionHandler
{
    private readonly TcpTunnelServer server;
    private readonly TcpClientFramingEndpoint endpoint;
    private readonly SystemNetEndpoint clientEndpoint;

    private bool firstClient;
    private Session? authenticatedSession;

    private int sessionIterationsToAcknowledge;

    public ServerConnectionHandler(
        TcpTunnelServer server,
        TcpClientFramingEndpoint endpoint,
        SystemNetEndpoint clientEndpoint)
    {
        this.server = server;
        this.endpoint = endpoint;
        this.clientEndpoint = clientEndpoint;
    }

    public TcpClientFramingEndpoint Endpoint
    {
        get => this.endpoint;
    }

    private int ClientIdx
    {
        get => this.firstClient ? 0 : 1;
    }

    public async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var packet = await this.endpoint.ReceiveMessageAsync(TcpTunnelServer.MaxReceivePacketSize);
                if (packet is null)
                    return;

                this.endpoint.HandlePing();
                var packetBuffer = packet.Value.Buffer;

                bool sessionWasAuthenticated;
                lock (this.server.SyncRoot)
                {
                    // Need to access the field within the lock, as it might be changed by
                    // a different connection handler.
                    sessionWasAuthenticated = this.authenticatedSession is not null;

                    if (sessionWasAuthenticated)
                    {
                        if (packetBuffer.Length >= 1 && packetBuffer.Span[0] is 0x03)
                        {
                            // Acknowledge the current session iteration.
                            if (this.sessionIterationsToAcknowledge <= 0)
                                throw new InvalidOperationException(); // Invalid message

                            this.sessionIterationsToAcknowledge--;
                        }
                        else if (packetBuffer.Length >= 1 &&
                            packetBuffer.Span[0] is Constants.TypeClientToClientCommunication)
                        {
                            // Client-to-client communication.
                            int clientIdx = this.ClientIdx;

                            // Forward the packet to the partner client, if available and if
                            // the current client has acknowledged the current session iteration.
                            if (this.authenticatedSession!.Clients[1 - clientIdx] is not null &&
                                this.sessionIterationsToAcknowledge is 0)
                            {
                                // Need to copy the data because it will be queued (and the buffer
                                // may be reused by the caller).
                                var resultPacket = packetBuffer.ToArray();
                                this.authenticatedSession.Clients[1 - clientIdx]!.endpoint.SendMessageByQueue(
                                    resultPacket);
                            }
                        }
                    }
                }

                if (!sessionWasAuthenticated)
                {
                    // Only allow authentication packets. Because we might need to do a
                    // (possibly expensive) authentication, we run this code outside of the
                    // lock, until we could actually authenticate the client.
                    if (packetBuffer.Length >= 2 + Constants.loginPrerequisiteBytes.Length + sizeof(int) &&
                        packetBuffer.Span[0] is 0x00)
                    {
                        // Authentication.
                        bool firstClient = packetBuffer.Span[1] is 0x00;
                        var clientPrerequisite = packetBuffer[2..][..Constants.loginPrerequisiteBytes.Length];

                        bool couldAuthenticate = false;
                        if (clientPrerequisite.Span.SequenceEqual(Constants.loginPrerequisiteBytes.Span))
                        {
                            int sessionId = BinaryPrimitives.ReadInt32BigEndian(
                                packetBuffer.Span[(2 + Constants.loginPrerequisiteBytes.Length)..]);
                            var enteredPasswordBytes = packetBuffer
                                [(2 + Constants.loginPrerequisiteBytes.Length + sizeof(int))..];

                            // Check if the session exists.
                            if (this.server.Sessions.TryGetValue(sessionId, out var session))
                            {
                                // Verify the session password. For this, we need to
                                // use FixedTimeEquals to prevent timing attacks.
                                if (CryptographicOperations.FixedTimeEquals(
                                    enteredPasswordBytes.Span,
                                    session.PasswordBytes.Span))
                                {
                                    // Client authenticated successfully for the given
                                    // session ID.
                                    couldAuthenticate = true;

                                    this.server.Logger?.Invoke(
                                        $"Proxy '{this.clientEndpoint}' authenticated for Session ID '{sessionId}' " +
                                        $"({(firstClient ? "proxy-listener" : "proxy-client")}).");

                                    // Enter the lock again. We don't need to check whether
                                    // authenticatedSession is still null, as it can only be
                                    // set to null by another connection handler, but not to
                                    // an non-null value.
                                    lock (this.server.SyncRoot)
                                    {
                                        this.authenticatedSession = session;
                                        this.firstClient = firstClient;

                                        int clientIdx = this.firstClient ? 0 : 1;

                                        // Check if an old client is present.
                                        // TODO: What should happen with that connection?
                                        // Currently we just de-authenticate it without
                                        // informing the corresponding client.
                                        if (session.Clients[clientIdx] is not null)
                                        {
                                            session.Clients[clientIdx]!.authenticatedSession = null;
                                            session.Clients[clientIdx] = null;
                                        }

                                        // Set the new client.
                                        session.Clients[clientIdx] = this;

                                        // Inform this client and the other client about
                                        // the new status.
                                        this.SendSessionStatus();

                                        if (session.Clients[1 - clientIdx] is { } partnerClient)
                                        {
                                            partnerClient.SendSessionStatus();

                                            // Increment the session iteration which the
                                            // clients need to acknowledge.
                                            checked
                                            {
                                                session.Clients[0]!.sessionIterationsToAcknowledge++;
                                                session.Clients[1]!.sessionIterationsToAcknowledge++;
                                            }
                                        }
                                    }
                                }
                            }

                            if (!couldAuthenticate)
                            {
                                var response = new byte[] { 0x01 };
                                this.endpoint.SendMessageByQueue(response);
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

    private void HandleClose()
    {
        lock (this.server.SyncRoot)
        {
            if (this.authenticatedSession is not null)
            {
                int clientIdx = this.ClientIdx;
                this.authenticatedSession.Clients[clientIdx] = null;

                if (this.authenticatedSession.Clients[1 - clientIdx] is not null)
                    this.authenticatedSession.Clients[1 - clientIdx]!.SendSessionStatus();

                this.authenticatedSession = null;
            }
        }
    }

    private void SendSessionStatus()
    {
        int clientIdx = this.ClientIdx;

        var response = new byte[2];

        response[0] = 0x02;
        response[1] = this.authenticatedSession!.Clients[1 - clientIdx] is not null ?
            (byte)0x01 :
            (byte)0x00;

        this.endpoint.SendMessageByQueue(response);
    }
}
