using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TcpTunnel.Networking;

internal class TcpClientEndpoint : Endpoint
{
    private readonly Func<CancellationToken, ValueTask>? connectHandler;

    private readonly Action? closeHandler;

    private readonly Func<NetworkStream, CancellationToken, ValueTask<Stream?>>? streamModifier;

    private readonly TcpClient client;

    private Stream? stream;

    private byte[]? currentReadBufferFromPool;

    public TcpClientEndpoint(
            TcpClient client,
            bool useSendQueue,
            bool usePingTimer,
            Func<CancellationToken, ValueTask>? connectHandler = null,
            Action? closeHandler = null,
            Func<NetworkStream, CancellationToken, ValueTask<Stream?>>? streamModifier = null)
        : base(useSendQueue, usePingTimer)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.connectHandler = connectHandler;
        this.closeHandler = closeHandler;
        this.streamModifier = streamModifier;
    }

    protected Stream? Stream
    {
        get => this.stream;
    }

    /// <summary>
    /// Reads at least one byte (returning as early as possible) up to the specified
    /// <paramref name="maxLength"/> from the stream, except when the end of stream has
    /// been reached or <paramref name="maxLength"/> is 0.
    /// </summary>
    /// <param name="maxLength"></param>
    /// <returns></returns>
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
            await this.stream!.ReadAsync(Memory<byte>.Empty, this.CancellationToken)
                .ConfigureAwait(false);

            // Get a receive buffer from the pool.
            int maxReceiveCount = maxLength is -1 ?
                Constants.ReceiveBufferSize :
                Math.Min(Constants.ReceiveBufferSize, maxLength);

            this.currentReadBufferFromPool = ArrayPool<byte>.Shared.Rent(maxReceiveCount);

            int count = await this.stream.ReadAsync(
                    this.currentReadBufferFromPool.AsMemory()
                        [..(maxLength is -1 ? this.currentReadBufferFromPool.Length : maxReceiveCount)],
                    this.CancellationToken)
                    .ConfigureAwait(false);

            if (count > 0)
            {
                var memory = this.currentReadBufferFromPool.AsMemory()[..count];
                var message = new ReceivedMessage(memory, ReceivedMessageType.Unknown);

                return message;
            }
            else
            {
                return null;
            }
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

    protected override async ValueTask HandleInitializationAsync()
    {
        await base.HandleInitializationAsync().ConfigureAwait(false);

        try
        {
            if (this.connectHandler is { } connectHandler)
                await connectHandler(this.CancellationToken).ConfigureAwait(false);

            var ns = new NetworkStream(this.client.Client, ownsSocket: false);
            this.stream = ns;

            if (this.streamModifier is not null)
            {
                var newStream = await this.streamModifier(ns, this.CancellationToken)
                        .ConfigureAwait(false);

                if (newStream is not null)
                    this.stream = newStream;
            }
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

    protected override async ValueTask HandleCloseAsync()
    {
        if (this.currentReadBufferFromPool is not null)
        {
            ArrayPool<byte>.Shared.Return(this.currentReadBufferFromPool);
            this.currentReadBufferFromPool = null;
        }

        await base.HandleCloseAsync().ConfigureAwait(false);

        // Dispose the stream.
        // Note: Disposing the TcpClient itself should be done by the caller
        // because he passed the instance to us.
        if (this.stream is not null)
            await this.stream.DisposeAsync().ConfigureAwait(false);

        this.closeHandler?.Invoke();
    }

    protected override Task CloseCoreAsync(bool normalClose)
    {
        if (normalClose)
        {
            // Shutdown the send channel.
            this.client.Client.Shutdown(SocketShutdown.Send);
        }
        else
        {
            // Close the socket with a timeout of 0, so that it resets
            // the connection.
            this.client.Client.Close(0);
        }

        return Task.CompletedTask;
    }

    protected override async Task SendMessageCoreAsync(
            Memory<byte> message,
            bool textMessage)
    {
        // We only support binary messages.
        if (textMessage)
        {
            throw new ArgumentException(
                    "Only binary messages are supported with the TcpClientEndpoint.");
        }

        try
        {
            await this.stream!.WriteAsync(message, this.CancellationToken)
                    .ConfigureAwait(false);

            await this.stream.FlushAsync(this.CancellationToken)
                    .ConfigureAwait(false);
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
}