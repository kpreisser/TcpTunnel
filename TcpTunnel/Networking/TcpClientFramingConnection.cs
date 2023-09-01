using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using TcpTunnel.Utils;

namespace TcpTunnel.Networking;

/// <summary>
/// An TCP connection that frames messages using a 4 byte length prefix.
/// </summary>
internal class TcpClientFramingConnection : TcpClientConnection
{
    // Create our own length buffer instead of using the ArrayPool for this, as this is
    // just a 4 byte buffer which will last as long as the connection instance.
    private readonly byte[] lengthBuffer = new byte[sizeof(int)];

    private byte[]? currentReadBufferFromPool;

    public TcpClientFramingConnection(
        TcpClient client,
        bool useSendQueue,
        bool usePingTimer,
        Func<CancellationToken, ValueTask>? connectHandler = null,
        Action? closeHandler = null,
        Func<NetworkStream, CancellationToken, ValueTask<Stream?>>? streamModifier = null)
        : base(client, useSendQueue, usePingTimer, connectHandler, closeHandler, streamModifier)
    {
    }

    /// <summary>
    /// Reads the complete next (framed) message, or throws if the message's size would exceed
    /// the specified <paramref name="maxLength"/> or the stream has ended prematurely.
    /// </summary>
    /// <param name="maxLength"></param>
    /// <returns></returns>
    public override async ValueTask<ReceivedMessage?> ReceiveMessageAsync(
        int maxLength,
        CancellationToken cancellationToken)
    {
        if (this.currentReadBufferFromPool is not null)
        {
            ArrayPool<byte>.Shared.Return(this.currentReadBufferFromPool);
            this.currentReadBufferFromPool = null;
        }

        try
        {
            // Read the 32-bit payload length.
            int readCount = await this.Stream!.ReadAtLeastAsync(
                this.lengthBuffer,
                this.lengthBuffer.Length,
                throwOnEndOfStream: false,
                cancellationToken)
                .ConfigureAwait(false);

            if (readCount is 0)
                return null;
            else if (readCount < this.lengthBuffer.Length)
                throw new EndOfStreamException();

            int payloadLength = BinaryPrimitives.ReadInt32BigEndian(this.lengthBuffer);

            if (payloadLength < 0 || payloadLength > maxLength)
                throw new InvalidDataException("Invalid frame length: " + payloadLength);

            // Rent a receive buffer from the pool.
            // We don't additionally wait until data is available (using
            // Memory<byte>.Empty) before renting a buffer, because in most cases there
            // should already be more data available after the payload length.
            // Additionally, it wouldn't buy us much e.g. as a DoS protection, since even
            // when there is only one additional byte available, we would also already
            // need to rent the buffer.
            this.currentReadBufferFromPool = ArrayPool<byte>.Shared.Rent(payloadLength);

            var bufferMemory = this.currentReadBufferFromPool.AsMemory()[..payloadLength];
            await this.Stream.ReadExactlyAsync(
                    bufferMemory,
                    cancellationToken)
                    .ConfigureAwait(false);

            return new ReceivedMessage(bufferMemory, ReceivedMessageType.ByteMessage);
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            // Ensure that a thread switch happens in case the current continuation is
            // called inline from CancellationTokenSource.Cancel(), which could lead to
            // deadlocks in certain situations (e.g. when holding some lock).
            await Task.Yield();
            throw;
        }
    }

    protected override ValueTask HandleCloseAsync()
    {
        if (this.currentReadBufferFromPool is not null)
        {
            ArrayPool<byte>.Shared.Return(this.currentReadBufferFromPool);
            this.currentReadBufferFromPool = null;
        }

        return base.HandleCloseAsync();
    }

    protected override async ValueTask SendMessageCoreAsync(
        Memory<byte> message,
        bool textMessage,
        CancellationToken cancellationToken)
    {
        int newFrameLength = sizeof(int) + message.Length;
        var newFrameArray = ArrayPool<byte>.Shared.Rent(newFrameLength);
        try
        {
            var newFrame = newFrameArray.AsMemory()[..newFrameLength];

            BinaryPrimitives.WriteInt32BigEndian(newFrame.Span, message.Length);
            message.CopyTo(newFrame[sizeof(int)..]);

            await base.SendMessageCoreAsync(newFrame, textMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(newFrameArray);
        }
    }
}
