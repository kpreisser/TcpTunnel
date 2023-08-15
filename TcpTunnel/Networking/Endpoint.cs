using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using TcpTunnel.Utils;

namespace TcpTunnel.Networking;

/// <summary>
/// Abstraction layer for a socket-like connection. This can be an WebSocket or a
/// TCP socket.
/// </summary>
/// <remarks>
/// It is possible to call Send methods while a Receive operation is still in progress
/// and vice versa.
/// </remarks>
internal abstract partial class Endpoint
{
    // TODO: Make this configurable.
    /// <summary>
    /// The maximum byte length of messages that can be queued before the connection
    /// is aborted, if the number of messages is more than
    /// <see cref="MinSendQueueItemCountForByteLengthCheck"/>.
    /// </summary>
    private const long MaxSendQueueByteLength = 20 * 1024 * 1024;

    /// <summary>
    /// The minimum number of enqueued messages before the check for
    /// <see cref="MaxSendQueueByteLength"/> is applied.
    /// </summary>
    /// <remarks>
    /// This is to ensure we can still send a single, large message that is larger
    /// than <see cref="MaxSendQueueByteLength"/>.
    /// </remarks>
    private const long MinSendQueueItemCountForByteLengthCheck = 4;

    private const int PingTimeout = 120 * 1000;

    protected static readonly Encoding TextMessageEncoding =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly bool useSendQueue;
    private readonly bool usePingTimer;

    private readonly object sendQueueSemaphoreSyncRoot = new();

    private CancellationTokenSource? cancellationTokenSource;

    private ConcurrentQueue<MessageQueueElement>? sendQueue;
    private ConcurrentQueue<MessageQueueElement>? sendQueueHighPriority;
    private SemaphoreSlim? sendQueueSemaphore;
    private bool sendQueueSemaphoreIsDisposed;

    // This field is volatile because it's checked in SendMessageByQueue() which
    // might be called from different threads, and we set and clear the field from
    // within RunEndpointAsync().
    private volatile Task? sendQueueWorkerTask;
    private long sendQueueByteLength; // accumulated byte length of queued messages

    private SemaphoreSlim? pingTimerSemaphore;
    private Task? pingTimerTask;
    private volatile bool pingTimerQuit;

    private volatile bool ignorePingTimeout;

    protected Endpoint(bool useSendQueue, bool usePingTimer)
        : base()
    {
        this.useSendQueue = useSendQueue;
        this.usePingTimer = usePingTimer;
    }

    protected CancellationToken CancellationToken
    {
        get => this.cancellationTokenSource!.Token;
    }

    /// <summary>
    /// Cancels pending I/O operations, which will abort the current connection.
    /// </summary>
    public virtual void Cancel()
    {
        // Public methods on CancellationTokenSource (except Dispose()) are
        // thread-safe, so we don't need a lock here.            
        try
        {
            this.cancellationTokenSource!.Cancel();
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

    /// <summary>
    /// Enqueues the given message to be sent to the client. This method is thread-safe.
    /// </summary>
    /// <remarks>
    /// This method does not block because it adds the message to
    /// a internal queue, from which a sender task takes the messages and calls
    /// <see cref="SendMessageCoreAsync(Memory{byte}, bool)"/>.
    /// This method can be called from multiple threads at the same time.
    /// 
    /// This is to avoid one client blocking all other clients when it does not read data
    /// and the send method uses locking instead of a separate task and queue.
    /// 
    /// If the client does not read data and the queued data exceeds a specific amount,
    /// the connection is aborted.
    /// 
    /// This method can throw exceptions if the connection has been aborted.
    /// </remarks>
    /// <param name="message"></param>
    /// <param name="highPriority">
    /// When <c>true</c>, specifies that the element has a high priority.
    /// Such elements will be processed by the sender task before other elements.
    /// </param>
    /// <returns></returns>
    public void SendMessageByQueue(string message, bool highPriority = false)
    {
        this.SendMessageByQueue(
                TextMessageEncoding.GetBytes(message),
                true,
                highPriority);
    }

    public void SendMessageByQueue(byte[] message, bool highPriority = false)
    {
        this.SendMessageByQueue(
                message is null ? default(Memory<byte>?) : message.AsMemory(),
                false,
                highPriority);
    }

    public void SendMessageByQueue(Memory<byte>? message, bool highPriority = false)
    {
        this.SendMessageByQueue(message, false, highPriority);
    }

    /// <summary>
    /// Sends a message asynchronously to the client. The returned task completes when
    /// the message has been sent completely to the client.
    /// </summary>
    /// <remarks>
    /// This method can only be called if useSendQueue is false.
    /// </remarks>
    /// <param name="message">The message to send.</param>
    public Task SendMessageAsync(string message)
    {
        return this.SendMessageAsync(
                TextMessageEncoding.GetBytes(message),
                true);
    }

    public Task SendMessageAsync(Memory<byte>? message)
    {
        return this.SendMessageAsync(message, false);
    }

    /// <summary>
    /// Asynchronously receives a message. The corresponding subclass defines what a
    /// message is.
    /// </summary>
    /// <remarks>
    /// Note that the returned's <see cref="ReceivedMessage.Buffer"/> may be reused for
    /// the next call to <see cref="ReceiveMessageAsync(int)"/>, so you should should
    /// either copy those bytes or use ByteMessage/StringMessage before the next call
    /// to this method.
    /// </remarks>
    /// <param name="maxLength">The maximum packet length.</param>
    /// <returns>The next packet, or <c>null</c> of the client closed the connection</returns>
    /// <exception cref="Exception">If an I/O error occurs</exception>
    public abstract Task<ReceivedMessage?> ReceiveMessageAsync(int maxLength);

    /// <summary>
    /// Resets the ping timer after receiving a client's PING message.
    /// </summary>
    public void HandlePing()
    {
        if (!this.usePingTimer || this.pingTimerTask is null)
            return;

        this.ignorePingTimeout = false;

        // Only release the semaphore if the ping timer already handled all previous
        // releases.
        if (this.pingTimerSemaphore!.CurrentCount is 0)
            this.pingTimerSemaphore.Release();
    }

    /// <summary>
    /// Instructs the ping timer to ignore timeouts from now on. To resume normal
    /// operation, call <see cref="HandlePing"/>.
    /// </summary>
    public void PausePings()
    {
        if (!this.usePingTimer || this.pingTimerTask is null)
            return;

        this.ignorePingTimeout = true;

        // Only release the semaphore if the ping timer already handled all previous
        // releases.
        if (this.pingTimerSemaphore!.CurrentCount is 0)
            this.pingTimerSemaphore.Release();
    }

    /// <summary>
    /// Runs this endpoint asynchronously.
    /// </summary>
    /// <remarks>
    /// You may call <see cref="SendMessageByQueue(byte[], bool)"/> (and other overloads)
    /// only while the <paramref name="runCallback"/> is executing.
    /// </remarks>
    /// <param name="runCallback"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task RunEndpointAsync(
            Func<Task> runCallback,
            CancellationToken cancellationToken = default)
    {
        // First, create the CTS as it might be needed by the ping task.
        this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        Thread.MemoryBarrier();

        // Start the ping task before running initalize (which e.g. might want
        // to negotiate SSL/TLS, which might already timeout, but we must not
        // yet send over this channel).
        this.StartPingTask();

        bool isNormalClose = true;
        try
        {
            await this.HandleInitializationAsync().ConfigureAwait(false);

            // After InitilaizeAsync() is finished, we can now start to send data.
            this.StartSendTask();

            // Call the callback that can receive data.
            await this.RunEndpointCoreAsync(runCallback).ConfigureAwait(false);
        }
        catch
        {
            isNormalClose = false;

            // Cancel pending I/O operations when an exception occured, so that we
            // can then close the connection.
            this.Cancel();

            throw;
        }
        finally
        {
            if (this.sendQueueWorkerTask is not null)
            {
                // Allow the send task to exit.
                this.sendQueue!.Enqueue(new MessageQueueElement()
                {
                    IsQueueEndElement = true
                });

                // Need to lock to ensure the sender task doesn't dispose the
                // semaphore while we might still be in the Release() call.
                lock (this.sendQueueSemaphoreSyncRoot)
                {
                    if (!this.sendQueueSemaphoreIsDisposed)
                        this.sendQueueSemaphore!.Release();
                }

                // Wait for the send task to complete. We wait asynchronously
                // because it could take some time (if it takes too long, the
                // ping timer will still be able to abort the connection).
                // Note: We must not wait synchronously (by using
                // .GetAwaiter().GetResult) because when a lot of threads did this,
                // we would block the thread pool (as we are ourselves an async
                // method running in a thread pool thread).
                await this.sendQueueWorkerTask.ConfigureAwait(false);
                this.sendQueueWorkerTask = null;
            }

            // Stop the ping timer.
            if (this.usePingTimer)
            {
                this.pingTimerQuit = true;
                this.pingTimerSemaphore!.Release();

                // Asynchronously wait for the task to finish. See comment above.
                await this.pingTimerTask!.ConfigureAwait(false);
                this.pingTimerTask = null;
                this.pingTimerSemaphore.Dispose();
                this.pingTimerSemaphore = null;
            }

            // Close the connection.
            try
            {
                // Treat it as abnormal close if the cancellation token was cancelled
                // in the meanwhile.
                bool normalClose = isNormalClose && !this.CancellationToken.IsCancellationRequested;
                await this.CloseCoreAsync(normalClose).ConfigureAwait(false);
            }
            catch
            {
                // Ignore.
            }

            await this.HandleCloseAsync().ConfigureAwait(false);

            // Dispose of the CTS.
            this.cancellationTokenSource.Dispose();
            this.cancellationTokenSource = null;
        }
    }

    protected virtual ValueTask HandleInitializationAsync()
    {
        // Do nothing.
        return default;
    }

    protected virtual ValueTask HandleCloseAsync()
    {
        // Do nothing.
        return default;
    }

    /// <summary>
    /// Subclasses overwrite this method to handle sending a message.
    /// This method can throw an exception if the underlying I/O operation fails.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="textMessage"></param>
    /// <returns></returns>
    protected abstract Task SendMessageCoreAsync(
            Memory<byte> message,
            bool textMessage);

    /// <summary>
    /// Closes the connection (or the send channel only, depending on the subclass).
    /// </summary>
    /// <remarks>
    /// Note: This method may be called again after it was called with
    /// <paramref name="normalClose"/> being <c>true</c>.
    /// </remarks>
    /// <returns></returns>
    protected abstract Task CloseCoreAsync(bool normalClose);

    protected virtual Task RunEndpointCoreAsync(Func<Task> runFunc)
    {
        return runFunc();
    }

    private void StartPingTask()
    {
        if (this.usePingTimer)
        {
            // Start a timer task.
            this.pingTimerSemaphore = new SemaphoreSlim(0);

            // Ensure the task can see the fields.
            Thread.MemoryBarrier();

            this.pingTimerTask = ExceptionUtils.StartTask(
                    this.RunPingTimerTaskAsync);
        }
    }

    private void StartSendTask()
    {
        if (this.useSendQueue)
        {
            // Start a send task.
            this.sendQueue = new ConcurrentQueue<MessageQueueElement>();
            this.sendQueueHighPriority = new ConcurrentQueue<MessageQueueElement>();
            this.sendQueueSemaphore = new SemaphoreSlim(0);

            // Ensure the task can see the fields.
            Thread.MemoryBarrier();

            this.sendQueueWorkerTask = ExceptionUtils.StartTask(
                    this.RunSendQueueWorkerTaskAsync);
        }
    }

    private void SendMessageByQueue(
            Memory<byte>? message,
            bool textMessage,
            bool highPriority)
    {
        if (!this.useSendQueue)
            throw new InvalidOperationException("Only blocking writes are supported.");
        if (this.sendQueueWorkerTask is null)
            throw new InvalidOperationException(
                "Endpoint has not yet been initialized or has already stopped. " +
                $"You may call this method only while the callback passed to " +
                $"{nameof(RunEndpointAsync)} is executing.");

        // If the size of queued messages was not too large, add the message to the queue.
        // Note: If this method is called again, the length of the next message will be
        // added again to the string length; therefore it is declared as long.
        if (message is not null)
        {
            long existingQueueStrLength = Interlocked.Add(
                    ref this.sendQueueByteLength,
                    message.Value.Length) -
                    message.Value.Length;

            if (this.sendQueue!.Count >= MinSendQueueItemCountForByteLengthCheck &&
                    existingQueueStrLength > MaxSendQueueByteLength)
            {
                // The queue is too large. Abort the connection; and ensure the SendTask does
                // not send anything after the last message (in case the queue is emptied in
                // the meanwhile and this method is called again)
                lock (this.sendQueueSemaphoreSyncRoot)
                {
                    if (!this.sendQueueSemaphoreIsDisposed)
                    {
                        this.sendQueue.Enqueue(new MessageQueueElement()
                        {
                            EnterFaultedState = true
                        });

                        this.sendQueueSemaphore!.Release();
                    }
                }

                this.Cancel();
                return;
            }
        }

        lock (this.sendQueueSemaphoreSyncRoot)
        {
            // If the send task has already exited, ingore the message.
            if (!this.sendQueueSemaphoreIsDisposed)
            {
                // Enqueue the message.
                // Elements with a high priority will be removed from the send
                // queue before other elements.
                (highPriority ? this.sendQueueHighPriority : this.sendQueue)!
                    .Enqueue(new MessageQueueElement()
                    {
                        Message = message,
                        IsTextMessage = textMessage
                    });

                this.sendQueueSemaphore!.Release();
            }
        }
    }

    private Task SendMessageAsync(Memory<byte>? message, bool textMessage)
    {
        if (this.useSendQueue)
            throw new InvalidOperationException("Only non-blocking writes are supported.");

        if (message is null)
            return this.CloseCoreAsync(true);
        else
            return this.SendMessageCoreAsync(message.Value, textMessage);
    }

    private async Task RunSendQueueWorkerTaskAsync()
    {
        try
        {
            bool isFaulted = false;

            while (true)
            {
                await this.sendQueueSemaphore!.WaitAsync().ConfigureAwait(false);

                // Process elements with a high priority first.
                if (!this.sendQueueHighPriority!.TryDequeue(out var el))
                {
                    // If no element with high priority is avaiable, process the
                    // next regular element.
                    if (!this.sendQueue!.TryDequeue(out el))
                        throw new InvalidOperationException(); // Should never happen
                }

                if (el.EnterFaultedState)
                    isFaulted = true;

                if (el.IsQueueEndElement)
                    return;

                if (!isFaulted)
                {
                    if (el.Message is null)
                    {
                        try
                        {
                            await this.CloseCoreAsync(true).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ignore.
                        }

                        isFaulted = true;
                    }
                    else
                    {
                        Interlocked.Add(ref this.sendQueueByteLength, -el.Message.Value.Length);

                        try
                        {
                            await this.SendMessageCoreAsync(el.Message.Value, el.IsTextMessage)
                                    .ConfigureAwait(false);
                        }
                        catch
                        {
                            // The connection is probably already closed/aborted.
                            // Note: Don't return here as that will dispose the
                            // messageQueueSemaphore, which will then cause the code in
                            // OnHandleClose() to throw a exception as it will expect that
                            // the semaphore is still active and try to release it. Also the
                            // queue might overflow. Instead, we ignore further writes so
                            // that the SendTask continues until a QueueEndElement is received.
                            isFaulted = true;
                        }
                    }
                }
            }
        }
        finally
        {
            lock (this.sendQueueSemaphoreSyncRoot)
            {
                this.sendQueueSemaphoreIsDisposed = true;
                this.sendQueueSemaphore!.Dispose();
            }

            // At this stage, no more messages will be added to the send queue, so we
            // empty it.
            while (this.sendQueue!.TryDequeue(out _))
            {
            }
        }
    }

    private async Task RunPingTimerTaskAsync()
    {
        bool ignorePingTimeout = this.ignorePingTimeout;

        while (true)
        {
            bool ok = await this.pingTimerSemaphore!.WaitAsync(PingTimeout)
                    .ConfigureAwait(false);

            if (this.pingTimerQuit)
            {
                break;
            }

            if (ok)
            {
                // The semaphore will have been released if HandlePing() or
                // PausePings() has been called.
                // Copy the ingorePingTimeout field after the semaphore has been
                // released.
                ignorePingTimeout = this.ignorePingTimeout;
            }
            else
            {
                // Client has run into a time-out; therefore we need to abort the
                // connection.
                if (!ignorePingTimeout)
                {
                    this.Cancel();
                    break;
                }
            }
        }
    }
}
