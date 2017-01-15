using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using TcpTunnel.Utils;

namespace TcpTunnel.SocketInterfaces
{
    internal class TcpClientFramingEndpoint : TcpClientEndpoint
    {
        private CompleteBytePacketReader packetReader;

        private byte[] lengthBuf = new byte[4];

        public TcpClientFramingEndpoint(TcpClient client, bool useSendQueue, bool usePingTimer,
            Func<NetworkStream, Task<Tuple<TcpClient, Stream>>> asyncStreamModifier = null) 
            : base(client, useSendQueue, usePingTimer, asyncStreamModifier)
        {
            this.packetReader = new CompleteBytePacketReader(base.ReceiveNextPacketAsync);
        }

        public override async Task<ReceivedPacket> ReceiveNextPacketAsync(int maxLength)
        {
            if (!await this.packetReader.ReadBytePacketAsync(new ArraySegment<byte>(this.lengthBuf)))
                return null;

            int payloadLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(this.lengthBuf, 0));
            if (payloadLength < 0 || payloadLength > maxLength)
                throw new InvalidDataException("Invalid frame length: " + payloadLength);

            byte[] payloadBuf = new byte[payloadLength]; // TODO: Reuse array
            if (!await this.packetReader.ReadBytePacketAsync(new ArraySegment<byte>(payloadBuf)))
                return null;

            return new ReceivedPacket(new ArraySegment<byte>(payloadBuf), ReceivedPacketType.ByteMessage);
        }

        protected override Task SendMessageInternalAsync(ArraySegment<byte> message, bool textMessage)
        {
            byte[] newFrame = new byte[4 + message.Count];
            BitConverterUtils.ToBytes(IPAddress.HostToNetworkOrder(message.Count), newFrame, 0);
            Array.Copy(message.Array, message.Offset, newFrame, 4, message.Count);
            return base.SendMessageInternalAsync(new ArraySegment<byte>(newFrame), textMessage);
        }
    }
}
