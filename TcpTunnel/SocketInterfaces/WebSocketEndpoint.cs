//--------------------------------------------------------------------------------------------------
// <copyright file="WebSocketEndpoint.cs" company="Traeger Industry Components GmbH">
//     This file is protected by Traeger Industry Components GmbH Copyright © 2013-2016.
// </copyright>
// <author>Konstantin Preißer</author>
//--------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.Utils;

namespace TcpTunnel.SocketInterfaces
{
    internal class WebSocketEndpoint : AbstractSocketEndpoint
    {
        // syncRoot is used to synchronize access to ws.Abort()
        private readonly object syncRoot = new object();

        private readonly WebSocket ws;
        private readonly byte[] byteBuf = new byte[Constants.ReceiveBufferSize];

        public WebSocketEndpoint(WebSocket ws, bool useSendQueue, bool usePingTimer)
            : base(useSendQueue, usePingTimer)
        {
            this.ws = ws;
        }

#pragma warning disable 1998
        public override async Task InitializeAsync()
#pragma warning restore 1998
        {
            // Do nothing.
        }

        public override async Task<ReceivedPacket> ReceiveNextPacketAsync(int maxLength)
        {
            ArraySegment<byte> byteBufSegment = new ArraySegment<byte>(byteBuf);

            // Buffering the input message
            MemoryStream messageBuffer = new MemoryStream(byteBuf.Length);

            while (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseSent)
            {
                WebSocketReceiveResult res = await ws.ReceiveAsync(byteBufSegment, CancellationToken.None);

                // Check if the message buffer gets too big - in this case abort the connection to prevent DoS.
                if (messageBuffer.Length + res.Count > maxLength)
                    throw new InvalidOperationException("The message size exceeds " + maxLength.ToString() + " bytes.");

                messageBuffer.Write(byteBuf, 0, res.Count);


                if (res.EndOfMessage)
                {
                    // Handle complete message.
                    if (res.MessageType == WebSocketMessageType.Close)
                        return null; // Close message 
                    else
                    {
                        bool isStringMsg = res.MessageType == WebSocketMessageType.Text;
                        ArraySegment<byte> msBuffer;
                        if (!messageBuffer.TryGetBuffer(out msBuffer))
                            throw new InvalidOperationException(); // Should not happen

                        ReceivedPacket packet = new ReceivedPacket(msBuffer, isStringMsg ? ReceivedPacketType.StringMessage : ReceivedPacketType.ByteMessage);
                        return packet;
                    }
                }
            }


            // Connection has been closed, so return null;
            return null;
        }

        
        protected override async Task SendMessageInternalAsync(ArraySegment<byte> message, bool textMessage)
        {
            await ws.SendAsync(message, textMessage ? WebSocketMessageType.Text : WebSocketMessageType.Binary, 
                true, CancellationToken.None);
                
        }

        protected override async Task CloseInternalAsync()
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }


        public override void Abort()
        {
            // Note: When calling ws.Abort(), it seems the current thread may continue the receive task,
            // so the Abort() method doesn't return until the receive task returned (but this doesn't work because
            // the receive task waits for the ping task). Therefore we need to queue this into the thread pool
            // and not wait for it as we want our Abort() method to return immediately.
            ThreadPool.QueueUserWorkItem((state) => {
                try {
                    // Ensure multiple threads cannot call Abort() at the same time.
                    lock (syncRoot) {
                        ws.Abort();
                    }
                }
                catch (Exception ex) when (ExceptionUtils.FilterException(ex)) {
                    // Ignore.
                    Debug.WriteLine(ex.ToString());
                }
            });
        }
    }
}