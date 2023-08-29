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
/// An endpoint that frames messages using a 4 byte length prefix.
/// </summary>
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

    /// <summary>
    /// Reads the complete next (framed) message, or throws if the message's size would exceed
    /// the specified <paramref name="maxLength"/> or the stream has ended prematurely.
    /// </summary>
    /// <param name="maxLength"></param>
    /// <returns></returns>
    public override async Task<ReceivedMessage?> ReceiveMessageAsync(
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
            // Wait until data is available.
            await this.Stream!.ReadAsync(Memory<byte>.Empty, cancellationToken)
                .ConfigureAwait(false);

            int payloadLength;
            const int lengthSize = 4;
            var lengthBuf = ArrayPool<byte>.Shared.Rent(lengthSize);
            try
            {
                int readCount = await this.Stream.ReadAtLeastAsync(
                    lengthBuf.AsMemory()[..lengthSize],
                    lengthSize,
                    throwOnEndOfStream: false,
                    cancellationToken);

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
            await this.Stream.ReadAsync(Memory<byte>.Empty, cancellationToken)
                .ConfigureAwait(false);

            // Get a receive buffer from the pool.
            this.currentReadBufferFromPool = ArrayPool<byte>.Shared.Rent(payloadLength);

            await this.Stream.ReadExactlyAsync(
                    this.currentReadBufferFromPool.AsMemory()[..payloadLength],
                    cancellationToken)
                    .ConfigureAwait(false);

            var memory = this.currentReadBufferFromPool.AsMemory()[..payloadLength];
            var message = new ReceivedMessage(memory, ReceivedMessageType.ByteMessage);

            return message;
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
