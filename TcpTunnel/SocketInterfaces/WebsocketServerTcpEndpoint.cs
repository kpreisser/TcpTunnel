using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace TcpTunnel.SocketInterfaces
{
    internal class WebsocketServerTcpEndpoint : TcpClientEndpoint
    {
        private const int MaxHttpRequestLength = 64 * 1024;

        private const string websocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";


        private static readonly byte[] HttpRequestTerminator = { 0x0d, 0x0a, 0x0d, 0x0a };

        private static readonly Encoding httpRequestEncoding = Encoding.UTF8;


        private readonly CompleteBytePacketReader packetReader;
        /// <summary>
        /// A buffer for receiving the frame header.
        /// </summary>
        private readonly byte[] frameHeaderReceiveBuffer = new byte[8];

        public WebsocketServerTcpEndpoint(
            TcpClient client, bool useSendQueue, bool usePingTimer,
            Func<NetworkStream, Task<Stream>> asyncStreamModifier = null)
            : base(client, useSendQueue, usePingTimer, asyncStreamModifier)
        {
            this.packetReader = new CompleteBytePacketReader(base.ReceiveNextPacketAsync);
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();

            // Establish the WebSocket connection by reading the client's http request and respond
            // with an appropriate response.
            // The client needs to send a GET request with the headers "Upgrade: WebSocket" and
            // "Sec-WebSocket-Version: 13", and the
            // "Connection" header must contain the "Upgrade" token.
            // It also must send the header "Sec-WebSocket-Key" with some arbitrary value.

            // Read until we got an 0x0D 0x0A 0x0D 0x0A.
            List<KeyValuePair<string, string>> headerKeyValues;
            using (var memBuf = new MemoryStream())
            {

                int foundTerminator = -1;
                while (true)
                {
                    var packet = await base.ReceiveNextPacketAsync(Constants.ReceiveBufferSize);
                    if (packet == null)
                        throw new InvalidDataException();
                    memBuf.Write(packet.RawBytes.Array, packet.RawBytes.Offset, packet.RawBytes.Count);

                    // Check if we have already a CRLF+CRLF.
                    for (int i = 0; i < memBuf.Length - HttpRequestTerminator.Length + 1; i++)
                    {
                        if (ByteArrayEquals(memBuf.GetBuffer(), i, HttpRequestTerminator, 0, HttpRequestTerminator.Length))
                        {
                            foundTerminator = i;
                            break;
                        }
                    }
                    if (foundTerminator >= 0)
                        break;
                    if (foundTerminator < 0 && memBuf.Length > MaxHttpRequestLength)
                        throw new InvalidDataException();
                }

                // The Client must not have sent extra bytes because it needs to await the response from the server.
                if (memBuf.Length > foundTerminator + HttpRequestTerminator.Length)
                    throw new InvalidDataException("Extra bytes detected after HTTP request");

                // Decode the request as UTF-8, replacing invalid sequences with replacement characters.
                string httpRequestString = httpRequestEncoding.GetString(memBuf.GetBuffer(), 0, (int)memBuf.Length - HttpRequestTerminator.Length);
                string[] httpLines = httpRequestString.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (!(httpLines[0].StartsWith("GET ") && httpLines[0].EndsWith(" HTTP/1.1")))
                    throw new InvalidDataException();

                // Decode the remaining lines into (lower-case) keys and trimmed values.
                headerKeyValues = new List<KeyValuePair<string, string>>();
                for (int i = 1; i < httpLines.Length; i++)
                {
                    string line = httpLines[i];
                    string value = null;
                    int posColon = line.IndexOf(":");
                    if (posColon > 0)
                    {
                        value = line.Substring(posColon + 1).Trim();
                        line = line.Substring(0, posColon).Trim().ToLowerInvariant();
                        headerKeyValues.Add(new KeyValuePair<string, string>(line, value));
                    }
                }
            }

            // Search for the "Upgrade: WebSocket" header.
            if (!((headerKeyValues.FirstOrDefault(s => string.Equals(s.Key, "Upgrade", StringComparison.OrdinalIgnoreCase))
                .Value?.ToLowerInvariant() == "websocket") == true))
                throw new InvalidDataException();

            // Search for the "Connection" header.
            if (!((headerKeyValues.FirstOrDefault(s => string.Equals(s.Key, "Connection", StringComparison.OrdinalIgnoreCase))
                .Value?.ToLowerInvariant().Contains("upgrade")) == true))
                throw new InvalidDataException();

            // Search for the "Sec-WebSocket-Version: 13" header.
            if (!((headerKeyValues.FirstOrDefault(s => string.Equals(s.Key, "Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase))
                .Value == "13") == true))
                throw new InvalidDataException();

            // Search for the "Sec-WebSocket-Key" header.
            string websocketKey = headerKeyValues.FirstOrDefault(
                s => string.Equals(s.Key, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)).Value;
            if (websocketKey == null)
                throw new InvalidDataException();


            // OK, now build the response.
            string key = websocketKey + websocketGuid;
            string keyResult;
            using (var sha1 = new SHA1Managed())
            {
                keyResult = Convert.ToBase64String(sha1.ComputeHash(httpRequestEncoding.GetBytes(key)));
            }

            var httpResponseSb = new StringBuilder();
            httpResponseSb.Append("HTTP/1.1 101 \r\n"); // Reason phrase is optional (e.g. Tomcat doesn't send it), but
            // there still must be a space after the status code.
            httpResponseSb.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            httpResponseSb.Append("Upgrade: websocket\r\n");
            httpResponseSb.Append("Connection: Upgrade\r\n");
            httpResponseSb.Append("Sec-WebSocket-Accept: ").Append(keyResult).Append("\r\n");
            // Send a CORS header because the client's request shouldn't include any stateful
            // information (like Cookies or HTTP auth).
            httpResponseSb.Append("Access-Control-Allow-Origin: *\r\n");
            // End the response with final CRLF.
            httpResponseSb.Append("\r\n");

            // Send the response. After this, initialization is finished.
            byte[] httpResponse = httpRequestEncoding.GetBytes(httpResponseSb.ToString());
            await base.SendMessageInternalAsync(new ArraySegment<byte>(httpResponse), false);
        }

        public override async Task<ReceivedPacket> ReceiveNextPacketAsync(int maxLength)
        {
            // Receive a frame. Note that we might receive a ping control frame, which means we
            // must emit a pong control frame as soon as possible. We do this by simply adding
            // it to the send queue (if the queue is used) or send it directly if the queue is
            // not used. A possible optimization when using the queue would be to ensure that
            // control frames in the queue are moved before non-control frames, but this should
            // not matter.

            while (true) {
                // A message may be fragmented by splitting it into multiple frames.
                // Control frames may be injected in a fragmented message (but a control messages
                // itself must not be fragmented).
                using (var messageBuffer = new MemoryStream())
                {
                    int messageOpcode = 0;
                    while (true)
                    {
                        // Receive the first two bytes which contain the opcode + fin flag, and the first
                        // byte of the payload length.
                        if (!await packetReader.ReadBytePacketAsync(new ArraySegment<byte>(frameHeaderReceiveBuffer, 0, 2)))
                            return null;

                        
                        int frameOpcode = frameHeaderReceiveBuffer[0] & 0xF;
                        bool isControlFrame = (frameOpcode & 0x8) != 0;

                        if (!isControlFrame)
                        {
                            if (messageOpcode == 0)
                            {
                                if (frameOpcode == 0)
                                    throw new InvalidDataException();
                                messageOpcode = frameOpcode;
                            }
                            else if (frameOpcode != 0)
                            {
                                throw new InvalidDataException();
                            }
                        }

                        bool fin = (frameHeaderReceiveBuffer[0] & 0x80) != 0;
                        if (isControlFrame && !fin) // Control messages must not be fragmented
                            throw new InvalidDataException();

                        // The client MUST use a mask.
                        bool mask = (frameHeaderReceiveBuffer[1] & 0x80) != 0;
                        if (!mask)
                            throw new InvalidDataException();

                        long payloadLength;
                        int payloadLengthByte = frameHeaderReceiveBuffer[1] & 0x7F;
                        if (payloadLengthByte < 126)
                        {
                            payloadLength = payloadLengthByte;
                        }
                        else if (payloadLengthByte == 126)
                        {
                            // Receive the next two bytes.
                            if (!await packetReader.ReadBytePacketAsync(new ArraySegment<byte>(frameHeaderReceiveBuffer, 0, 2)))
                                throw new InvalidDataException();

                            payloadLength = unchecked((ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(frameHeaderReceiveBuffer, 0)));
                            if (payloadLength < 126)
                                throw new InvalidDataException();
                        }
                        else
                        {
                            // Receive the next 8 bytes.
                            if (!await packetReader.ReadBytePacketAsync(new ArraySegment<byte>(frameHeaderReceiveBuffer, 0, 8)))
                                throw new InvalidDataException();

                            payloadLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt64(frameHeaderReceiveBuffer, 0));
                            if (payloadLength < 0 || payloadLength < 0x10000)
                                throw new InvalidOperationException();
                        }
                        // Check the max payload length. If we received an continuation frame, add the buffered message length.
                        if ((!isControlFrame ? messageBuffer.Length : 0) + payloadLength > maxLength)
                            throw new InvalidDataException("Max message length exceeded");

                        // Get the masking key.
                        if (!await packetReader.ReadBytePacketAsync(new ArraySegment<byte>(frameHeaderReceiveBuffer, 0, 4)))
                            throw new InvalidDataException();

                        var maskingKey = new ArraySegment<byte>(frameHeaderReceiveBuffer, 0, 4);

                        // Receive the payload.
                        // TODO use a buffer array
                        byte[] payloadContent = new byte[payloadLength];
                        if (!await packetReader.ReadBytePacketAsync(new ArraySegment<byte>(payloadContent)))
                            throw new InvalidDataException();

                        // Demask the payload.
                        for (int i = 0; i < payloadContent.Length; i++)
                            payloadContent[i] ^= maskingKey.Array[maskingKey.Offset + i % maskingKey.Count];


                        if (isControlFrame)
                        {
                            // If the frame is a ping frame, we need to send a pong frame.
                            if (frameOpcode == 0x9)
                            {
                                // TODO
                            }
                            else if (frameOpcode == 0xA)
                            {
                                // PONG - Ignore.
                            }
                            else
                            {
                                // We currently also treat close frames as connetion abort.
                                throw new InvalidDataException();
                            }
                        }
                        else 
                        {
                            messageBuffer.Write(payloadContent, 0, payloadContent.Length);

                            // If the FIN flag is not set, we need to receive the next frame.
                            if (fin)
                                break;
                        }
                    }

                    if (messageOpcode == 0x1 || messageOpcode == 0x2)
                    {
                        // Return the message.
                        return new ReceivedPacket(new ArraySegment<byte>(messageBuffer.ToArray()),
                            messageOpcode == 0x1 ? ReceivedPacketType.StringMessage : ReceivedPacketType.ByteMessage);
                    }
                    else
                    {
                        throw new InvalidDataException();
                    }
                }
            }
        }

        protected override async Task SendMessageInternalAsync(ArraySegment<byte> message, bool textMessage)
        {
            using (var ms = new MemoryStream())
            {
                var frame = BuildFrame(true, textMessage ? 0x1 : 0x2, message);
                await base.SendMessageInternalAsync(new ArraySegment<byte>(frame), false);
            }
        }



        private static byte[] BuildFrame(bool fin, int opcode, ArraySegment<byte> payload)
        {
            using (var ms = new MemoryStream())
            {
                // First byte: set the FIN flag and the opcode.
                ms.WriteByte((byte)(opcode | (fin ? 0x80 : 0)));

                // The server must not use masking, so the masking flag must not be set.
                // Encoded payload length (unsigned):
                // When < 126: 1 byte; when < 65535: 2 bytes; 8 bytes otherwise
                if (payload.Count < 126)
                {
                    ms.WriteByte((byte)payload.Count);
                }
                else if (payload.Count < 0x10000)
                {
                    ms.WriteByte(126);
                    ms.WriteByte((byte)((payload.Count >> 0x100) & 0xFF));
                    ms.WriteByte((byte)(payload.Count & 0xFF));
                }
                else
                {
                    ms.WriteByte(127);
                    byte[] bytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((long)payload.Count));
                    ms.Write(bytes, 0, bytes.Length);
                }

                // Payload data
                ms.Write(payload.Array, payload.Offset, payload.Count);

                return ms.ToArray();
            }
        }


        private static bool ByteArrayEquals(byte[] source, int sourceOffset, byte[] dest, int destOffset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (source[sourceOffset + i] != dest[destOffset + i])
                    return false;
            }
            return true;
        }
    }
}
