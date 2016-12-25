//--------------------------------------------------------------------------------------------------
// <copyright file="TcpClientEndpoint.cs" company="Traeger Industry Components GmbH">
//     This file is protected by Traeger Industry Components GmbH Copyright © 2013-2016.
// </copyright>
// <author>Konstantin Preißer</author>
//--------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using TcpTunnel.Utils;

namespace TcpTunnel.SocketInterfaces
{
    internal class TcpClientEndpoint : AbstractSocketEndpoint
    {
        private readonly object syncRoot = new object();

        private readonly TcpClient client;
        private Stream stream;
        private Func<NetworkStream, Task<Stream>> asyncStreamModifier;

        private byte[] readBuf = new byte[Constants.ReceiveBufferSize];

        public TcpClientEndpoint(TcpClient client, bool useSendQueue, bool usePingTimer,
            Func<NetworkStream, Task<Stream>> asyncStreamModifier = null)
            : base(useSendQueue, usePingTimer)
        {
            this.client = client;
            this.asyncStreamModifier = asyncStreamModifier;
        }
        
        public override async Task InitializeAsync()
        {
            var ns = this.client.GetStream();
            this.stream = ns;
            if (asyncStreamModifier != null)
                this.stream = await asyncStreamModifier(ns);
        }

        public override async Task<ReceivedPacket> ReceiveNextPacketAsync(int maxLength)
        {
            if (maxLength <= 0)
                return null;

            // Note: NetworkStream.Read(buf, offset, 0) does not return immediatly if no new data is available.
            int count = await stream.ReadAsync(readBuf, 0, maxLength == -1 ? readBuf.Length : Math.Min(readBuf.Length, maxLength));
            if (count > 0)
            {
                ArraySegment<byte> segment = new ArraySegment<byte>(readBuf, 0, count);
                ReceivedPacket packet = new ReceivedPacket(segment, ReceivedPacketType.Unknown);
                return packet;
            }
            else
            {
                return null;
            }
        }


        public override void Abort()
        {
            lock (syncRoot)
            {
                try
                {
                    client.Client.Close(0); // Close the socket so that it resets the connection.
                    client.Close();
                }
                catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                {
                    // Ignore.
                    System.Diagnostics.Debug.WriteLine(ex.ToString());
                }
            }
        }

#pragma warning disable 1998
        protected async override Task CloseInternalAsync()
#pragma warning restore 1998
        {
            lock (syncRoot)
            {
                stream.Close();
                client.Close();
            }
        }

        // We only support binary messages.
        protected override async Task SendMessageInternalAsync(byte[] message, bool textMessage)
        {
            if (textMessage)
                throw new ArgumentException("Only binary messages are supported with the TcpClientEndpoint.");

            await stream.WriteAsync(message, 0, message.Length);
        }


    }
}