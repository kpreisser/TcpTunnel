using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using TcpTunnel.Utils;

namespace TcpTunnel.Proxy;

internal class ProxyTunnelConnection
{
    private readonly TcpClient remoteClient;

    // can be null if we don't need to connect
    private readonly Func<CancellationToken, ValueTask>? connectHandler;
    private readonly Action<Memory<byte>>? receiveHandler;
    private readonly Action<int> transmitWindowUpdateHandler;
    private readonly Action<bool /* isAbort */> connectionFinishedHandler;

    private readonly object syncRoot = new();
    private readonly CancellationTokenSource cts;
    private readonly ConcurrentQueue<Memory<byte>> transmitPacketQueue;
    private readonly SemaphoreSlim transmitPacketQueueSemaphore;

    /// <summary>
    /// A queue which contains at most 2 entries. The sum of all entries is
    /// the remaining receive window.
    /// The receive task only starts to receive if the window is at least 1/48
    /// of the default window size.
    /// </summary>
    private readonly ConcurrentQueue<int> receiveWindowQueue;
    private readonly SemaphoreSlim receiveWindowQueueSemaphore;

    private Task? receiveTask;
    private bool receiveTaskStopped;
    private bool transmitTaskStopped;
    private bool ctsDisposed;

    public ProxyTunnelConnection(
        TcpClient remoteClient,
        Func<CancellationToken, ValueTask>? connectHandler,
        Action<Memory<byte>>? receiveHandler,
        Action<int> transmitWindowUpdateHandler,
        Action<bool> connectionFinishedHandler)
    {
        this.remoteClient = remoteClient;
        this.connectHandler = connectHandler;
        this.receiveHandler = receiveHandler;
        this.transmitWindowUpdateHandler = transmitWindowUpdateHandler;
        this.connectionFinishedHandler = connectionFinishedHandler;

        this.cts = new();
        this.transmitPacketQueue = new();
        this.transmitPacketQueueSemaphore = new(0);
        this.receiveWindowQueue = new();
        this.receiveWindowQueueSemaphore = new(0);
    }

    public bool IsSendChannelClosed
    {
        get;
        set;
    }

    public bool IsReceiveChannelClosed
    {
        get;
        set;
    }

    public void Start()
    {
        // Add the initial window size.
        this.UpdateReceiveWindow(Constants.InitialWindowSize);

        // Create the receive task (the transmit task will be created by the receive task).
        this.receiveTask = ExceptionUtils.StartTask(this.RunReceiveTaskAsync);
    }

    public async ValueTask StopAsync()
    {
        if (this.receiveTask is null)
            throw new InvalidOperationException();

        this.Abort();
        await this.receiveTask;
    }

    public void EnqueuePacket(Memory<byte> packet)
    {
        // Packet's length can be 0 to shutdown the connection after sending all
        // enqueued packets.
        // TODO: Check if the data amount is valid according to the currently
        // available window size.
        lock (this.syncRoot)
        {
            if (!this.transmitTaskStopped)
            {
                this.transmitPacketQueue.Enqueue(packet);
                this.transmitPacketQueueSemaphore.Release();
            }
        }
    }

    public void UpdateReceiveWindow(int newWindow)
    {
        if (newWindow < 0)
            throw new ArgumentOutOfRangeException(nameof(newWindow));

        lock (this.syncRoot)
        {
            if (!this.receiveTaskStopped)
            {
                int window = 0;

                while (this.receiveWindowQueueSemaphore.Wait(0))
                {
                    if (!this.receiveWindowQueue.TryDequeue(out int entry))
                        throw new InvalidOperationException();

                    checked
                    {
                        window += entry;
                    }
                }

                checked
                {
                    window += newWindow;
                }

                // The new window size must not be greater than the initial window size.
                if (window > Constants.InitialWindowSize)
                    throw new InvalidOperationException();

                this.receiveWindowQueue.Enqueue(window);
                this.receiveWindowQueueSemaphore.Release();
            }
        }
    }

    private void Abort()
    {
        // Close the TCP connection immediately with a RST.
        // This can be used to abort the connection even if we are currently blocked
        // in sending data.
        lock (this.syncRoot)
        {
            if (this.ctsDisposed)
                return;

            try
            {
                this.cts.Cancel();
            }
            catch (AggregateException)
            {
                // Ignore.
                // This can occur with some implementations, e.g. registered callbacks
                // from  WebSocket operations using HTTP.sys (from ASP.NET Core) can
                // throw here when calling Cancel() and the IWebHost has already been
                // disposed.
            }
        }
    }

    private async Task RunReceiveTaskAsync()
    {
        bool isAbort = false;

        try
        {
            var remoteClientStream = default(NetworkStream);
            var transmitTask = default(Task);

            try
            {
                if (this.connectHandler is not null)
                    await this.connectHandler(this.cts.Token);

                // After connecting, create the transmit task.
                remoteClientStream = new NetworkStream(remoteClient.Client, ownsSocket: false);

                transmitTask = ExceptionUtils.StartTask(() => this.RunTransmitTaskAsync(remoteClientStream));

                // Now start to receive.
                int availableWindow = 0;

                while (true)
                {
                    bool wait = false;

                    while (true)
                    {
                        // Collect the available window.
                        for (int i = 0; ; i++)
                        {
                            if (i is 0 && wait)
                                await this.receiveWindowQueueSemaphore.WaitAsync(this.cts.Token);
                            else if (!this.receiveWindowQueueSemaphore.Wait(0))
                                break;

                            if (!this.receiveWindowQueue.TryDequeue(out int entry))
                                throw new InvalidOperationException();

                            checked
                            {
                                availableWindow += entry;
                            }
                        }

                        if (availableWindow >= Constants.InitialWindowSize / 48)
                            break;

                        // Insufficient window is available, so we need to wait.
                        wait = true;
                    }

                    // Wait until new data is available.
                    await remoteClientStream.ReadAsync(Memory<byte>.Empty, this.cts.Token);

                    var receiveBuffer = ArrayPool<byte>.Shared.Rent(
                        Math.Min(Constants.ReceiveBufferSize, availableWindow));

                    try
                    {
                        int received = await remoteClientStream.ReadAsync(
                            receiveBuffer.AsMemory()[..Math.Min(availableWindow, receiveBuffer.Length)],
                            this.cts.Token);

                        if (received is 0)
                            break; // Connection has been closed.

                        availableWindow -= received;

                        this.receiveHandler?.Invoke(receiveBuffer.AsMemory()[..received]);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(receiveBuffer);
                    }
                }
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                // Ensure that a thread switch happens in case the current continuation is
                // called inline from CancellationTokenSource.Cancel(), which could lead to
                // deadlocks in certain situations (e.g. when holding some lock).
                await Task.Yield();

                isAbort = true;
            }
            finally
            {
                lock (this.syncRoot)
                {
                    this.receiveTaskStopped = true;
                    this.receiveWindowQueueSemaphore.Dispose();
                }

                // Invoke the receive handler with an empty array, to notify it that the
                // connection was half-closed. If it was aborted, we will report it instead
                // in the connection finished handler (so that when the event is raised, the
                // connection is already finished and can be removed from the dictionary).
                if (!isAbort)
                    this.receiveHandler?.Invoke(Memory<byte>.Empty);

                // Wait for the transmit task to stop. This can take a while if the partner
                // proxy doesn't yet send a close.
                if (transmitTask is not null)
                {
                    if (isAbort)
                    {
                        // When the connection was aborted, allow the transmit task to exit
                        // if it is currently blocked waiting for new entries.
                        this.Abort();
                    }

                    await transmitTask;
                }

                await (remoteClientStream?.DisposeAsync() ?? default);
                this.transmitPacketQueueSemaphore.Dispose();
            }
        }
        finally
        {
            // When we broke out due to an exception, ensure to reset the connection.
            // We also need to check whether cancellation has been requested, because
            // it could happen that Abort() is called after we already entered the
            // above 'finally' clause and waited for the transmit task to finish.
            bool ctsWasCanceled = this.cts.Token.IsCancellationRequested;

            try
            {
                if (isAbort || ctsWasCanceled)
                    this.remoteClient.Client.Close(0);

                this.remoteClient.Dispose();
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                // Ignore
            }

            // Dispose the CTS.
            lock (this.syncRoot)
            {
                this.ctsDisposed = true;
            }

            this.cts.Dispose();

            // Notify that the connection is finished, and report whether it was aborted.
            this.connectionFinishedHandler?.Invoke(isAbort || ctsWasCanceled);
        }
    }

    private async Task RunTransmitTaskAsync(NetworkStream remoteClientStream)
    {
        try
        {
            while (true)
            {
                await this.transmitPacketQueueSemaphore.WaitAsync(this.cts.Token);
                if (!this.transmitPacketQueue.TryDequeue(out var packet))
                    throw new InvalidOperationException();

                if (packet.Length is 0)
                {
                    // Close the connection and return.
                    this.remoteClient.Client.Shutdown(SocketShutdown.Send);
                    return;
                }

                // Send the packet.
                await remoteClientStream.WriteAsync(packet, this.cts.Token);
                await remoteClientStream.FlushAsync(this.cts.Token);

                // Update the transmit window.
                this.transmitWindowUpdateHandler?.Invoke(packet.Length);
            }
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            // Ensure that a thread switch happens in case the current continuation is
            // called inline from CancellationTokenSource.Cancel(), in which case we
            // might unexpectedly hold a lock on syncRoot.
            await Task.Yield();

            // Additionally, abort it here to ensure that when the receive task is
            // already waiting for the transmit task to complete (when the remote
            // already half-closed the connection), it can then report the abort
            // to the partner proxy.
            this.Abort();
        }
        finally
        {
            lock (this.syncRoot)
            {
                this.transmitTaskStopped = true;
            }
        }
    }
}
