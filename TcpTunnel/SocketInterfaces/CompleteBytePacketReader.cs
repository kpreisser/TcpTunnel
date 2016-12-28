//--------------------------------------------------------------------------------------------------
// <copyright file="CompleteBytePacketReader.cs" company="Traeger Industry Components GmbH">
//     This file is protected by Traeger Industry Components GmbH Copyright © 2013-2016.
// </copyright>
// <author>Konstantin Preißer</author>
//--------------------------------------------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace TcpTunnel.SocketInterfaces
{

    /// <summary>
    /// Utility class to read a byte array with a fixed length from a read function that
    /// may return byte arrays in arbitrary length.
    /// </summary>
    internal class CompleteBytePacketReader
    {
        private ArraySegment<byte>? currentSegment;
        private int currentSegmentPosition = 0;
        private Func<int, Task<ReceivedPacket>> receiveNextPacketAsync;

        public CompleteBytePacketReader(Func<int, Task<ReceivedPacket>> receiveNextPacketAsync)
        {
            this.receiveNextPacketAsync = receiveNextPacketAsync;
        }

        public async Task<bool> ReadBytePacketAsync(ArraySegment<byte> bytesToRead)
        {
            int bytesRead = 0;
            while (bytesRead < bytesToRead.Count)
            {
                if (!currentSegment.HasValue)
                {
                    // Read the next packet. We use -1 as maxLength because for the TcpClientEndpoint it uses a 8 KB buffer.
                    var next = await receiveNextPacketAsync(-1);
                    if (next == null)
                        return false;
                    currentSegment = next.RawBytes;
                    currentSegmentPosition = 0;
                }

                var seg = currentSegment.Value;
                // Copy bytes from the current segment.
                int copyBytes = Math.Min(bytesToRead.Count - bytesRead, seg.Count - currentSegmentPosition);
                Array.Copy(seg.Array, seg.Offset + currentSegmentPosition, bytesToRead.Array, bytesToRead.Offset + bytesRead, copyBytes);
                currentSegmentPosition += copyBytes;
                if (currentSegmentPosition == seg.Count)
                {
                    // The segment is finished.
                    currentSegment = null;
                    currentSegmentPosition = 0;
                }

                bytesRead += copyBytes;
            }

            return true;
        }
    }
}