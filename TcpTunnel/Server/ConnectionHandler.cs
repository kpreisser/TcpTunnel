using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using TcpTunnel.SocketInterfaces;
using TcpTunnel.Utils;

namespace TcpTunnel.Server
{
    /*
     * PROTOCOL (Frame Payload):
     * 0x00: Server Communication:
     *    -> 0x00: (Client to Server) Authentication + Client-Type (0x00 or 0x01) + Login-Prerequisite-String + Session-Key
     *    -> 0x01: (Server to Client) Authentication failed, try again.
     *    -> 0x02 + [8 bytes Session Iteration]: (Server to Client) Session Status (sent after successfull authentication and during runtime if status changes):
     *           -> 0x00: Remote client is available.
     *           -> 0x01: Remote client is unavailable.
     * 0x01 + [8 bytes Session Iteration (only for Client to Server)]: Remove client communication. The server forwards the packet to the remote client if available.
     * 
     * 0xFF: Reserved for Ping.
     */
    internal class ConnectionHandler
    {
        private readonly TcpTunnelServer server;
        private readonly TcpClientFramingEndpoint endpoint;

        private bool firstClient;
        private Session authenticatedSession;


        public ConnectionHandler(TcpTunnelServer server, TcpClientFramingEndpoint endpoint)
        {
            this.server = server;
            this.endpoint = endpoint;

        }

        public async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    var packet = await this.endpoint.ReceiveNextPacketAsync(TcpTunnelServer.MaxReceivePacketSize);
                    if (packet == null)
                        return;

                    this.endpoint.HandlePing();

                    if (this.authenticatedSession == null)
                    {
                        // Only allow authentication packets.
                        if (packet.RawBytes.Count > 0 && packet.RawBytes.Array[packet.RawBytes.Offset + 0] == 0x00)
                        {
                            if (packet.RawBytes.Count >= 3 + Constants.loginPrerequisiteBytes.Count + sizeof(int)
                                && packet.RawBytes.Array[packet.RawBytes.Offset + 1] == 0x00)
                            {
                                // Authentication.
                                bool firstClient = packet.RawBytes.Array[packet.RawBytes.Offset + 2] == 0x00;

                                ArraySegment<byte> clientPrerequisite = new ArraySegment<byte>(packet.RawBytes.Array,
                                    packet.RawBytes.Offset + 3, Constants.loginPrerequisiteBytes.Count);
                                if (Constants.ComparePassword(clientPrerequisite, Constants.loginPrerequisiteBytes))
                                {
                                    int sessionID = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(
                                        packet.RawBytes.Array, packet.RawBytes.Offset + 3 + Constants.loginPrerequisiteBytes.Count));
                                    string sessionPassword = Encoding.UTF8.GetString(packet.RawBytes.Array,
                                        packet.RawBytes.Offset + 3 + Constants.loginPrerequisiteBytes.Count + sizeof(int),
                                        packet.RawBytes.Count - 3 - Constants.loginPrerequisiteBytes.Count - sizeof(int));

                                    // Check if the session exists.
                                    lock (this.server.SyncRoot)
                                    {
                                        Session session;
                                        if (this.server.sessions.TryGetValue(sessionID, out session))
                                        {
                                            // Check the session password.
                                            byte[] enteredPassword = Encoding.Unicode.GetBytes(sessionPassword);
                                            byte[] correctPassword = Encoding.Unicode.GetBytes(session.Password);

                                            if (Constants.ComparePassword(new ArraySegment<byte>(enteredPassword),
                                                new ArraySegment<byte>(correctPassword)))
                                            {
                                                this.authenticatedSession = session;
                                                this.firstClient = firstClient;

                                                int clientIdx = this.firstClient ? 0 : 1;

                                                // This is not needed any more due to the session iteration.
                                                //// Remove the old client, if present.
                                                //session.Clients[clientIdx] = null;

                                                //// Inform the other client about the new status.
                                                //if (session.Clients[1 - clientIdx] != null)
                                                //    session.Clients[1 - clientIdx].SendSessionStatus();

                                                // Check if an old client is present. TODO: What should happen with that connection?
                                                
                                                if (session.Clients[clientIdx] != null)
                                                {
                                                    session.Clients[clientIdx].authenticatedSession = null;
                                                    session.Clients[clientIdx] = null;
                                                }

                                                // Set the new client.
                                                session.Clients[clientIdx] = this;
                                                session.UpdateIteration();

                                                // Inform this client and the other client about the new status.
                                                SendSessionStatus();
                                                if (session.Clients[1 - clientIdx] != null)
                                                    session.Clients[1 - clientIdx].SendSessionStatus();
                                            }
                                        }

                                        if (this.authenticatedSession == null)
                                        {
                                            byte[] response = new byte[] { 0x00, 0x01 };
                                            this.endpoint.SendMessageByQueue(response);
                                        }
                                    }
                                }

                            }
                        }
                    }
                    else
                    {
                        if (packet.RawBytes.Count >= 1 + sizeof(long)
                            && packet.RawBytes.Array[packet.RawBytes.Offset + 0] == 0x01)
                        {
                            lock (this.server.SyncRoot)
                            {
                                int clientIdx = GetClientIdx();

                                // Forward the packet to the other client, if available and if the session
                                // iteration is correct.
                                long sessionIteration = IPAddress.NetworkToHostOrder(BitConverter.ToInt64(
                                    packet.RawBytes.Array, packet.RawBytes.Offset + 1));

                                if (this.authenticatedSession.Clients[1 - clientIdx] != null
                                    && this.authenticatedSession.CurrentIteration == sessionIteration) {
                                    // We don't need to send the current iteration because the client already knows it
                                    // from the session status message.
                                    byte[] resultPacket = new byte[1 + (packet.RawBytes.Count - (1 + sizeof(long)))];
                                    resultPacket[0] = 0x01;
                                    Array.Copy(packet.RawBytes.Array, packet.RawBytes.Offset + 1 + sizeof(long),
                                        resultPacket, 1, resultPacket.Length - 1);
                                    this.authenticatedSession.Clients[1 - clientIdx].endpoint.SendMessageByQueue(resultPacket);
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                HandleClose();
            }
        }

        private void HandleClose()
        {
            lock (this.server.SyncRoot)
            {
                if (this.authenticatedSession != null)
                {
                    int clientIdx = GetClientIdx();
                    this.authenticatedSession.Clients[clientIdx] = null;
                    if (this.authenticatedSession.Clients[1 - clientIdx] != null)
                        this.authenticatedSession.Clients[1 - clientIdx].SendSessionStatus();

                    this.authenticatedSession = null;
                }
            }
        }

        private void SendSessionStatus()
        {
            int clientIdx = GetClientIdx();
            byte[] response = new byte[2 + sizeof(long) + 1];
            response[0] = 0x00;
            response[1] = 0x02;
            BitConverterUtils.ToBytes(this.authenticatedSession.CurrentIteration, response, 2);
            response[2 + sizeof(long)] = this.authenticatedSession.Clients[1 - clientIdx] != null ? (byte)0x00 : (byte)0x01;
            this.endpoint.SendMessageByQueue(response);
        }

        private int GetClientIdx() => this.firstClient ? 0 : 1;
    }
}
