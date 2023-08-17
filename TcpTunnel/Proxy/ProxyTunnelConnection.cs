using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using TcpTunnel.Utils;

namespace TcpTunnel.Proxy;

internal class ProxyTunnelConnection<T>
{
    private readonly TcpClient remoteClient;

    // can be null if we don't need to connect
    private readonly Func<CancellationToken, ValueTask>? connectHandler;
    private readonly Action<Memory<byte>> receiveHandler;
    private readonly Action<int> transmitWindowUpdateHandler;
    private readonly Action<bool /* isAbort */> connectionFinishedHandler;

    private readonly object syncRoot = new();
    private readonly CancellationTokenSource cts;
    private readonly ConcurrentQueue<ReadOnlyMemory<byte>> transmitDataQueue;
    private readonly SemaphoreSlim transmitDataQueueSemaphore;

    private readonly SemaphoreSlim receiveWindowAvailableSemaphore;

    /// <summary>
    /// Window that is available for the receive task.
    /// </summary>
    /// <remarks>
    /// The receive task only starts to receive if the window is at least
    /// the <see cref="Constants.WindowThreshold"/>.
    /// </remarks>
    private int receiveWindowAvailable = Constants.InitialWindowSize;

    /// <summary>
    /// Window that is currently available for enqueuing transmit data.
    /// </summary>
    private int transmitWindowAvailable = Constants.InitialWindowSize;

    private Task? receiveTask;
    private bool receiveTaskStopped;
    private bool transmitTaskStopped;
    private bool ctsDisposed;

    private T? data;

    public ProxyTunnelConnection(
        TcpClient remoteClient,
        Func<CancellationToken, ValueTask>? connectHandler,
        Action<Memory<byte>> receiveHandler,
        Action<int> transmitWindowUpdateHandler,
        Action<bool> connectionFinishedHandler)
    {
        this.remoteClient = remoteClient;
        this.connectHandler = connectHandler;
        this.receiveHandler = receiveHandler;
        this.transmitWindowUpdateHandler = transmitWindowUpdateHandler;
        this.connectionFinishedHandler = connectionFinishedHandler;

        this.cts = new();
        this.transmitDataQueue = new();
        this.transmitDataQueueSemaphore = new(0);
        this.receiveWindowAvailableSemaphore = new(0);
    }

    public ref T? Data
    {
        get => ref this.data;
    }

    public void Start()
    {
        // Create the receive task (the transmit task will later be created by the
        // receive task).
        this.receiveTask = ExceptionUtils.StartTask(this.RunReceiveTaskAsync);
    }

    public async ValueTask StopAsync()
    {
        if (this.receiveTask is null)
            throw new InvalidOperationException();

        this.Abort();
        await this.receiveTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="data"></param>
    /// <exception cref="InvalidOperationException">
    /// The queued data's length would exceed the initial window size.
    /// </exception>
    public void EnqueueTransmitData(ReadOnlyMemory<byte> data)
    {
        // data's length can be 0 to shutdown the connection after sending all
        // enqueued packets.
        lock (this.syncRoot)
        {
            if (this.transmitTaskStopped)
                return;

            // Verify that the data to be queued doesn't exceed the available
            // transmit window. Otherwise, the partner proxy wouldn't work
            // correctly or might be malicious.
            if (data.Length > this.transmitWindowAvailable)
                throw new InvalidOperationException();

            this.transmitWindowAvailable -= data.Length;
            this.transmitDataQueue.Enqueue(data);
            this.transmitDataQueueSemaphore.Release();
        }
    }

    public void UpdateReceiveWindow(int newWindow)
    {
        if (newWindow is < 0 or > Constants.InitialWindowSize)
            throw new ArgumentOutOfRangeException(nameof(newWindow));

        lock (this.syncRoot)
        {
            if (this.receiveTaskStopped)
                return;

            int resultingWindow = Interlocked.Add(ref this.receiveWindowAvailable, newWindow);

            // The new window size must not be greater than the initial window size.
            if (resultingWindow > Constants.InitialWindowSize || resultingWindow < newWindow)
                throw new InvalidOperationException();

            // Additionally, release the semaphore if the receive task is waiting
            // for it.
            if (this.receiveWindowAvailableSemaphore.CurrentCount is 0)
                this.receiveWindowAvailableSemaphore.Release();
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

            this.cts.CancelAndIgnoreAggregateException();
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

                remoteClientStream = new NetworkStream(remoteClient.Client, ownsSocket: false);

                // After connecting, create the transmit task.
                transmitTask = ExceptionUtils.StartTask(() => this.RunTransmitTaskAsync(remoteClientStream));

                // Now start to receive.
                int currentWindow = 0;

                while (true)
                {
                    // Wait until more data is available.
                    await remoteClientStream.ReadAsync(Memory<byte>.Empty, this.cts.Token);

                    // Collect the available window, then receive the data.
                    currentWindow = await CollectAvailableWindowAsync(currentWindow);

                    var receiveBuffer = ArrayPool<byte>.Shared.Rent(
                        Math.Min(Constants.ReceiveBufferSize, currentWindow));

                    try
                    {
                        // We can call the synchronous Read method without passing a CancellationToken since
                        // it should return immediately (as we know there is data available), which should be
                        // less expensive.
                        int received = remoteClientStream.Read(
                            receiveBuffer.AsSpan()[..Math.Min(receiveBuffer.Length, currentWindow)]);

                        if (received is 0)
                            break; // Connection has been closed.

                        currentWindow -= received;

                        this.receiveHandler(receiveBuffer.AsMemory()[..received]);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(receiveBuffer);
                    }
                }
            }
            catch
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
                    this.receiveWindowAvailableSemaphore.Dispose();
                }

                // Invoke the receive handler with an empty array, to notify it that the
                // connection was half-closed. If it was aborted, we will report it instead
                // in the connection finished handler (so that when the event is raised, the
                // connection is already finished and can be removed from the dictionary).
                if (!isAbort)
                    this.receiveHandler(Memory<byte>.Empty);

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
                this.transmitDataQueueSemaphore.Dispose();
            }
        }
        finally
        {
            // When we broke out due to an exception, ensure to reset the connection.
            // We also need to check whether cancellation has been requested, because
            // it could happen that Abort() was called after we already entered the
            // above 'finally' clause (due to a normal close) and waited for the
            // transmit task to finish.
            bool ctsWasCanceled;

            lock (this.syncRoot)
            {
                ctsWasCanceled = this.cts.Token.IsCancellationRequested;
                this.ctsDisposed = true;
            }

            this.cts.Dispose();

            try
            {
                if (isAbort || ctsWasCanceled)
                    this.remoteClient.Client.Close(0);

                this.remoteClient.Dispose();
            }
            catch
            {
                // Ignore
            }

            // Notify that the connection is finished, and report whether it was aborted.
            this.connectionFinishedHandler?.Invoke(isAbort || ctsWasCanceled);
        }

        async ValueTask<int> CollectAvailableWindowAsync(int currentWindow)
        {
            while (true)
            {
                checked
                {
                    currentWindow += Interlocked.Exchange(ref this.receiveWindowAvailable, 0);
                }

                // The new window size must not be greater than the initial window size.
                if (currentWindow > Constants.InitialWindowSize)
                    throw new InvalidOperationException();

                // Only return when the available window is at least as high as
                // the window threshold, to avoid scattered packets.
                if (currentWindow >= Constants.WindowThreshold)
                    return currentWindow;

                // Otherwise, we need to wait until sufficient window is available.
                await this.receiveWindowAvailableSemaphore.WaitAsync(this.cts.Token);
            }
        }
    }

    private async Task RunTransmitTaskAsync(NetworkStream remoteClientStream)
    {
        try
        {
            int accumulatedWindow = 0;

            while (true)
            {
                await this.transmitDataQueueSemaphore.WaitAsync(this.cts.Token);
                if (!this.transmitDataQueue.TryDequeue(out var data))
                    throw new InvalidOperationException();

                if (data.Length is 0)
                {
                    // Close the connection and return.
                    this.remoteClient.Client.Shutdown(SocketShutdown.Send);
                    return;
                }

                // Send the packet.
                await remoteClientStream.WriteAsync(data, this.cts.Token);
                await remoteClientStream.FlushAsync(this.cts.Token);

                // Update the transmit window.
                checked
                {
                    accumulatedWindow += data.Length;
                }

                // Only raise the window update event if we reached the threshold,
                // to avoid flooding the connection with small window updates.
                if (accumulatedWindow >= Constants.WindowThreshold)
                {
                    this.UpdateTransmitWindow(accumulatedWindow);
                    accumulatedWindow = 0;
                }
            }
        }
        catch
        {
            // Ensure that a thread switch happens in case the current continuation is
            // called inline from CancellationTokenSource.Cancel(), in which case we
            // might unexpectedly hold a lock on syncRoot.
            await Task.Yield();

            // Additionally, abort the connection here to ensure that when the receive
            // task is already waiting for the transmit task to complete (when the
            // remote already half-closed the connection), it can then report the abort
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

    private void UpdateTransmitWindow(int availableWindow)
    {
        // Need to increment the available window before calling the handler.
        lock (this.syncRoot)
            this.transmitWindowAvailable += availableWindow;

        this.transmitWindowUpdateHandler?.Invoke(availableWindow);
    }
}
