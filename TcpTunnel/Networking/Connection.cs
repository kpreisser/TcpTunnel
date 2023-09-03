using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// The connection has a built-in send queue that can be used to queue packets to
/// be sent without having to wait for the previous send operation to finish.
/// Additionally, a ping timer is implemented which can be used to detect whether
/// the peer is still available.
/// </remarks>
internal abstract partial class Connection
{
    // TODO: Make this configurable.
    /// <summary>
    /// The maximum byte length of messages that can be queued before the connection
    /// is aborted, if the number of messages is more than
    /// <see cref="MinSendQueueItemCountForByteLengthCheck"/>.
    /// </summary>
    /// <remarks>
    /// This is to prevent us from buffering more data endlessly if the peer doesn't
    /// read from the socket. However, the limit is relatively large to not
    /// erroneously abort the connection when we actually want to send that much messages.
    /// </remarks>
    private const long MaxSendQueueByteLength = 50 * 1024 * 1024;

    /// <summary>
    /// The minimum number of enqueued messages before the check for
    /// <see cref="MaxSendQueueByteLength"/> is applied.
    /// </summary>
    /// <remarks>
    /// This is to ensure we can still send a single, large message that is larger
    /// than <see cref="MaxSendQueueByteLength"/>.
    /// </remarks>
    private const long MinSendQueueItemCountForByteLengthCheck = 100;

    private const int PingTimeout = 120 * 1000;

    protected static readonly Encoding TextMessageEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly bool useSendQueue;
    private readonly bool usePingTimer;

    private readonly object sendQueueSyncRoot = new();

    private CancellationTokenSource? cancellationTokenSource;

    private Queue<MessageQueueElement>? sendQueue;
    private Queue<MessageQueueElement>? sendQueueHighPriority;
    private SemaphoreSlim? sendQueueSemaphore;
    private bool sendStopped;

    private Task? sendQueueWorkerTask;
    private long sendQueueByteLength; // accumulated byte length of queued messages

    private SemaphoreSlim? pingTimerSemaphore;
    private Task? pingTimerTask;
    private bool pingTimerQuit;

    private bool ignorePingTimeout;

    protected Connection(bool useSendQueue, bool usePingTimer)
        : base()
    {
        this.useSendQueue = useSendQueue;
        this.usePingTimer = usePingTimer;
    }

    private CancellationToken CancellationToken
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
        this.cancellationTokenSource!.CancelAndIgnoreAggregateException();
    }

    /// <summary>
    /// Enqueues the given message to be sent to the peer. This method is thread-safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method does not block, because it adds the message to
    /// a internal queue from which a sender task takes the messages and calls
    /// <see cref="SendMessageCoreAsync(Memory{byte}, bool, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// If the peer does not read data and the queued data exceeds a specific amount,
    /// the connection will be aborted.
    /// </para>
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
    /// Sends a message asynchronously to the peer. You can only call this method again
    /// once the returned <see cref="Task"/> is completed.
    /// </summary>
    /// <remarks>
    /// This method can only be called if <c>useSendQueue</c> was passed as <c>false</c>.
    /// </remarks>
    /// <param name="message">The message to send.</param>
    public ValueTask SendMessageAsync(string message)
    {
        return this.SendMessageAsync(
            TextMessageEncoding.GetBytes(message),
            true);
    }

    public ValueTask SendMessageAsync(Memory<byte>? message)
    {
        return this.SendMessageAsync(message, false);
    }

    /// <summary>
    /// Asynchronously receives a message. The corresponding subclass defines what a
    /// message is.
    /// </summary>
    /// <remarks>
    /// Note that the returned's <see cref="ReceivedMessage.Buffer"/> may be reused for
    /// the next call to <see cref="ReceiveMessageAsync(int, CancellationToken)"/>, so
    /// you should should either copy those bytes or use ByteMessage/StringMessage
    /// before the next call to this method.
    /// </remarks>
    /// <param name="maxLength">The maximum packet length.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The next packet, or <c>null</c> of the client closed the connection</returns>
    /// <exception cref="Exception">If an I/O error occurs</exception>
    public abstract ValueTask<ReceivedMessage?> ReceiveMessageAsync(
        int maxLength,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resets the ping timer after receiving a client's ping message.
    /// </summary>
    public void HandlePing()
    {
        if (!this.usePingTimer || this.pingTimerTask is null)
            return;

        this.ignorePingTimeout = false;
        Thread.MemoryBarrier();

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
        Thread.MemoryBarrier();

        // Only release the semaphore if the ping timer already handled all previous
        // releases.
        if (this.pingTimerSemaphore!.CurrentCount is 0)
            this.pingTimerSemaphore.Release();
    }

    /// <summary>
    /// Runs this connection asynchronously.
    /// </summary>
    /// <remarks>
    /// You may call <see cref="SendMessageByQueue(byte[], bool)"/> (and other overloads)
    /// only while the <paramref name="runConnectionCallback"/> hasn't completed.
    /// </remarks>
    /// <param name="runConnectionCallback"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task RunConnectionAsync(
        Func<CancellationToken, Task> runConnectionCallback,
        CancellationToken cancellationToken = default)
    {
        // First, create the CTS as it might be needed by the ping task.
        this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Thread.MemoryBarrier();

        // Start the ping task before running initalize (which e.g. might want
        // to negotiate SSL/TLS, which might already timeout, but we must not
        // yet send over this channel from the send task).
        this.StartPingTask();

        bool isNormalClose = true;
        try
        {
            await this.HandleInitializationAsync(this.CancellationToken)
                .ConfigureAwait(false);

            // After HandleInitializationAsync() is finished, we can now start to send data.
            this.StartSendTask();

            // Call the callback that can receive data.
            await runConnectionCallback(this.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            try
            {
                isNormalClose = false;

                // Cancel pending I/O operations when an exception occured, so that we
                // can then close the connection.
                this.Cancel();

                throw;
            }
            catch (Exception ex2) when (ex2.CanCatch() && false)
            {
                // We need a separate exception filter to prevent the finally handler
                // from being called in case of an OOME being thrown in the above catch
                // block. For example, if CancellationTokenSource.Cancel() throws an OOME,
                // we might otherwise start to execute the finally handler which would
                // wait for the tasks to exit, but since the CTS wouldn't have been
                // cancelled, we might wait infinitely.
                throw;
            }
        }
        finally
        {
            if (this.sendQueueWorkerTask is not null)
            {
                // Allow the send task to exit (we will close the connection later).
                lock (this.sendQueueSyncRoot)
                {
                    this.sendQueue!.Enqueue(new MessageQueueElement()
                    {
                        ExitSendTask = true
                    });
                }

                this.sendQueueSemaphore!.Release();

                // Wait for the send task to complete. We wait asynchronously
                // because it could take some time (if it takes too long, the
                // ping timer will still be able to abort the connection).
                // Note: We must not wait synchronously (by using
                // .GetAwaiter().GetResult) because when a lot of threads did this,
                // we would block the thread pool (as we are ourselves an async
                // method running in a thread pool thread).
                await this.sendQueueWorkerTask.ConfigureAwait(false);
                this.sendQueueSemaphore.Dispose();
                this.sendQueueSemaphore = null;

                lock (this.sendQueueSyncRoot)
                    this.sendQueueWorkerTask = null;
            }

            // Stop the ping timer.
            if (this.usePingTimer)
            {
                this.pingTimerQuit = true;
                Thread.MemoryBarrier();

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
                await this.CloseCoreAsync(normalClose, this.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                // Ignore.
            }

            await this.HandleCloseAsync().ConfigureAwait(false);

            // Dispose of the CTS.
            this.cancellationTokenSource.Dispose();
            this.cancellationTokenSource = null;
        }
    }

    protected virtual ValueTask HandleInitializationAsync(CancellationToken cancellationToken)
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
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected abstract ValueTask SendMessageCoreAsync(
        Memory<byte> message,
        bool textMessage,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes the connection (or the send channel only, depending on the subclass).
    /// </summary>
    /// <remarks>
    /// Note: This method may be called again after it was called with
    /// <paramref name="normalClose"/> being <c>true</c>.
    /// </remarks>
    /// <returns></returns>
    protected abstract ValueTask CloseCoreAsync(
        bool normalClose,
        CancellationToken cancellationToken);

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
            this.sendQueue = new Queue<MessageQueueElement>();
            this.sendQueueHighPriority = new Queue<MessageQueueElement>();
            this.sendQueueSemaphore = new SemaphoreSlim(0);

            // Ensure the task can see the fields.
            Thread.MemoryBarrier();

            lock (this.sendQueueSyncRoot)
            {
                this.sendQueueWorkerTask = ExceptionUtils.StartTask(
                    this.RunSendQueueWorkerTaskAsync);
            }
        }
    }

    private void SendMessageByQueue(
            Memory<byte>? message,
            bool textMessage,
            bool highPriority)
    {
        if (!this.useSendQueue)
            throw new InvalidOperationException("Only non-queued writes are supported.");

        bool cancelConnection = false;
        lock (this.sendQueueSyncRoot)
        {
            if (this.sendQueueWorkerTask is null)
            {
                throw new InvalidOperationException(
                    $"Connection has not yet been initialized or has already stopped. " +
                    $"You may call this method only while the callback passed to " +
                    $"{nameof(RunConnectionAsync)} is executing.");
            }

            // If the send task has already exited, ignore the message.
            if (this.sendStopped)
                return;

            if (message is not null)
            {
                int messageLength = message.Value.Length;

                if (this.sendQueue!.Count >= MinSendQueueItemCountForByteLengthCheck &&
                    messageLength > MaxSendQueueByteLength - this.sendQueueByteLength)
                {
                    // The queue would be too large. Abort the connection and ensure
                    // no more data will be queued.
                    this.sendStopped = true;
                    cancelConnection = true;
                }
                else
                {
                    this.sendQueueByteLength += messageLength;
                }
            }

            if (!cancelConnection)
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

        if (cancelConnection)
        {
            // Cancel the connection outside of the lock.
            this.Cancel();
        }
    }

    private ValueTask SendMessageAsync(Memory<byte>? message, bool textMessage)
    {
        if (this.useSendQueue)
            throw new InvalidOperationException("Only queued writes are supported.");

        if (message is null)
            return this.CloseCoreAsync(true, this.CancellationToken);
        else
            return this.SendMessageCoreAsync(message.Value, textMessage, this.CancellationToken);
    }

    private async Task RunSendQueueWorkerTaskAsync()
    {
        try
        {
            while (true)
            {
                await this.sendQueueSemaphore!.WaitAsync(this.CancellationToken)
                    .ConfigureAwait(false);

                MessageQueueElement el;
                lock (this.sendQueueSyncRoot)
                {
                    // Process elements with a high priority first.
                    if (!this.sendQueueHighPriority!.TryDequeue(out el))
                        el = this.sendQueue!.Dequeue();

                    if (el.ExitSendTask)
                        return;

                    if (el.Message is not null)
                        this.sendQueueByteLength -= el.Message.Value.Length;
                }

                if (el.Message is null)
                {
                    // Close the connection and return.
                    await this.CloseCoreAsync(true, this.CancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                await this.SendMessageCoreAsync(
                    el.Message.Value,
                    el.IsTextMessage,
                    this.CancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            // Ignore the exception.
            try
            {
                // Ensure that a thread switch happens in case the current continuation is
                // called inline from CancellationTokenSource.Cancel(), which could lead to
                // deadlocks in certain situations (e.g. when holding some lock).
                await Task.Yield();
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
            lock (this.sendQueueSyncRoot)
            {
                this.sendStopped = true;

                // At this stage, no more messages will be added to the send queue, so we
                // clear it.
                this.sendQueue!.Clear();
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

            Thread.MemoryBarrier();

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
