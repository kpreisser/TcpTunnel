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
    private readonly Socket remoteSocket;

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

    /// <summary>
    /// Specifies whether an empty packet to shut down the transmit channel has
    /// already been enqueued.
    /// </summary>
    private bool transmitShutdown;

    private Task? receiveTask;
    private bool receiveTaskStopped;
    private bool transmitTaskStopped;
    private bool ctsDisposed;

    private T? data;

    public ProxyTunnelConnection(
        Socket remoteSocket,
        Func<CancellationToken, ValueTask>? connectHandler,
        Action<Memory<byte>> receiveHandler,
        Action<int> transmitWindowUpdateHandler,
        Action<bool> connectionFinishedHandler)
    {
        this.remoteSocket = remoteSocket;
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
    /// <exception cref="ArgumentException">
    /// The queued data's length would exceed the initial window size.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// This method is called again even though it has already been called with
    /// an empty packet (which indicates the transmit channel is to be shut down).
    /// </exception>
    public void EnqueueTransmitData(ReadOnlyMemory<byte> data)
    {
        // `data`'s length can be 0 to shutdown the transmit channel after sending all
        // enqueued packets.
        lock (this.syncRoot)
        {
            // Verify that we don't already have added an empty transmit packet,
            // which indicates that the transmit channel is to be shut down.
            // This especially ensures that we won't enqueue an unlimited amount
            // of empty packets.
            if (this.transmitShutdown)
                throw new InvalidOperationException("The transmit channel is already shutting down.");

            if (this.transmitTaskStopped)
                return;

            // Verify that the data to be queued doesn't exceed the available
            // transmit window. Otherwise, the partner proxy wouldn't work
            // correctly or might be malicious.
            if (data.Length > this.transmitWindowAvailable)
                throw new ArgumentException("The data would exceed the available transmit window size.");

            // Note: The maximum number of packets in the transmit data queue at any given
            // time (and the max. times the transmit data semaphore may be released) is the
            // number of bytes of the default window size, plus one for the empty packet
            // that shuts down the transmit channel (i.e., `Constants.InitialWindowSize + 1`).
            // This may theoretically happen if the partner proxy sends data packets with
            // only 1 byte length.
            this.transmitWindowAvailable -= data.Length;
            this.transmitDataQueue.Enqueue(data);
            this.transmitDataQueueSemaphore.Release();

            // If data's length is 0, the transmit channel is to be shut down, so we may
            // not enqueue any more transmit packets after this.
            if (data.Length is 0)
                this.transmitShutdown = true;
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
                throw new ArgumentException();

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
            var remoteSocketStream = default(NetworkStream);
            var transmitTask = default(Task);

            try
            {
                if (this.connectHandler is not null)
                    await this.connectHandler(this.cts.Token);

                remoteSocketStream = new NetworkStream(this.remoteSocket, ownsSocket: false);

                // After connecting, create the transmit task.
                transmitTask = ExceptionUtils.StartTask(
                    () => this.RunTransmitTaskAsync(remoteSocketStream));

                // Now start to receive.
                int currentWindow = 0;

                while (true)
                {
                    // Wait until more data is available.
                    _ = await remoteSocketStream.ReadAsync(Memory<byte>.Empty, this.cts.Token);

                    // Collect the available window, then receive the data.
                    currentWindow = await CollectAvailableWindowAsync(currentWindow);

                    var receiveBuffer = ArrayPool<byte>.Shared.Rent(
                        Math.Min(Constants.ReceiveBufferSize, currentWindow));

                    try
                    {
                        // We can call the synchronous Read method without passing a
                        // CancellationToken since it should return immediately (as we know
                        // there is data available), which should be less expensive.
                        int received = remoteSocketStream.Read(
                            receiveBuffer.AsSpan()[..Math.Min(receiveBuffer.Length, currentWindow)]);

                        // Invoke the receive handler. When the received length is 0, the partner
                        // proxy will know that the connection was half-closed. If it was aborted
                        // (indicated by ReadAsync() throwing an exception), we will report that
                        // instead in the connection finished handler (so that when the event is
                        // raised, the connection is already finished and can be removed from
                        // the dictionary).
                        this.receiveHandler(receiveBuffer.AsMemory()[..received]);

                        if (received is 0)
                            break; // Connection has been closed.

                        currentWindow -= received;
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
                    this.receiveWindowAvailableSemaphore.Dispose();
                }

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

                await (remoteSocketStream?.DisposeAsync() ?? default);
                this.transmitDataQueueSemaphore.Dispose();
            }
        }
        catch (Exception ex) when (ex.CanCatch() && false)
        {
            // We need a separate exception filter to prevent the finally handler
            // from being called in case of an OOME.
            throw;
        }
        finally
        {
            // When we broke out due to an exception, ensure to reset the connection.
            // We also need to check whether cancellation has been requested, because
            // it could happen that Abort() was called after we already entered the
            // above 'finally' clause (due to a normal close) and waited for the
            // transmit task to finish (for example, if the transmit task encountered
            // an exception and therefore called Abort() which we need to report to
            // the partner proxy, or if the partner proxy sent an abort message).
            lock (this.syncRoot)
            {
                isAbort = isAbort || this.cts.Token.IsCancellationRequested;
                this.ctsDisposed = true;
            }

            this.cts.Dispose();

            try
            {
                if (isAbort)
                    this.remoteSocket.Close(0);

                this.remoteSocket.Dispose();
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                // Ignore
            }

            // Notify that the connection is finished, and report whether it was aborted.
            this.connectionFinishedHandler?.Invoke(isAbort);
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

    private async Task RunTransmitTaskAsync(NetworkStream remoteSocketStream)
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
                    this.remoteSocket.Shutdown(SocketShutdown.Send);
                    return;
                }

                // Send the packet.
                await remoteSocketStream.WriteAsync(data, this.cts.Token);
                await remoteSocketStream.FlushAsync(this.cts.Token);

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
        catch (Exception ex) when (ex.CanCatch())
        {
            try
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
            catch (Exception ex2) when (ex2.CanCatch() && false)
            {
                // We need a separate exception filter to prevent the finally handler
                // from being called in case of an OOME being thrown in the above catch
                // block.
                throw;
            }
        }
        finally
        {
            lock (this.syncRoot)
            {
                this.transmitTaskStopped = true;

                // At this stage, no more packets will be added to the transmit queue, so we
                // clear it.
                this.transmitDataQueue.Clear();
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
