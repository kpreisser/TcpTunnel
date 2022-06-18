using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using TcpTunnel.Utils;

namespace TcpTunnel.Networking;

internal class TcpClientFramingEndpoint : TcpClientEndpoint
{
    private byte[]? currentReadBufferFromPool;

    public TcpClientFramingEndpoint(
        TcpClient client,
        bool useSendQueue,
        bool usePingTimer,
        Func<CancellationToken, ValueTask>? connectHandler = null,
        Action? closeHandler = null,
        Func<NetworkStream, CancellationToken, ValueTask<Stream?>>? streamModifier = null)
        : base(client, useSendQueue, usePingTimer, connectHandler, closeHandler, streamModifier)
    {
    }

    public override async Task<ReceivedMessage?> ReceiveMessageAsync(int maxLength)
    {
        if (this.currentReadBufferFromPool is not null)
        {
            ArrayPool<byte>.Shared.Return(this.currentReadBufferFromPool);
            this.currentReadBufferFromPool = null;
        }

        try
        {
            // Wait until data is available.
            await this.Stream!.ReadAsync(Memory<byte>.Empty, this.CancellationToken)
                .ConfigureAwait(false);

            int payloadLength;
            const int lengthSize = 4;
            var lengthBuf = ArrayPool<byte>.Shared.Rent(lengthSize);
            try
            {
                int readCount = await this.Stream.ReadAtLeastAsync(
                    lengthBuf.AsMemory()[..lengthSize],
                    lengthSize,
                    throwOnEndOfStrem: false,
                    this.CancellationToken);

                if (readCount is 0)
                    return null;
                else if (readCount < lengthSize)
                    throw new EndOfStreamException();

                payloadLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(lengthBuf);
            }

            if (payloadLength < 0 || payloadLength > maxLength)
                throw new InvalidDataException("Invalid frame length: " + payloadLength);

            // Wait until data is available.
            await this.Stream.ReadAsync(Memory<byte>.Empty, this.CancellationToken)
                .ConfigureAwait(false);

            // Get a receive buffer from the pool.
            this.currentReadBufferFromPool = ArrayPool<byte>.Shared.Rent(payloadLength);

            await this.Stream.ReadExactlyAsync(
                    this.currentReadBufferFromPool.AsMemory()[..payloadLength],
                    this.CancellationToken)
                    .ConfigureAwait(false);

            var memory = this.currentReadBufferFromPool.AsMemory()[..payloadLength];
            var message = new ReceivedMessage(memory, ReceivedMessageType.ByteMessage);

            return message;
        }
        catch
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

    protected override async Task SendMessageCoreAsync(Memory<byte> message, bool textMessage)
    {
        int newFrameLength = sizeof(int) + message.Length;
        var newFrameArray = ArrayPool<byte>.Shared.Rent(newFrameLength);
        try
        {
            var newFrame = newFrameArray.AsMemory()[..newFrameLength];

            BinaryPrimitives.WriteInt32BigEndian(newFrame.Span, message.Length);
            message.CopyTo(newFrame[sizeof(int)..]);

            await base.SendMessageCoreAsync(newFrame, textMessage)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(newFrameArray);
        }
    }
}
