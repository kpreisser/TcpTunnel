using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.Utils;

namespace TcpTunnel.SocketInterfaces
{

    /// <summary>
    /// Abstraction layer for a socket-like connection. This can be an Websocket, a LongPolling
    /// handler or a TCP socket.
    /// Note that the Send/Close methods may only be called from one thread at the same time.
    /// However, Receive events could be called at the same time from a receiver thread
    /// (but also only one per time).
    /// </summary>
    internal abstract class AbstractSocketEndpoint
    {
        public static readonly Encoding TextMessageEncoding = Encoding.UTF8;

        // TODO: make this configurable
        // Max byte length of messages that can be queued before the connection is aborted.
        private const long MaxClientMessageQueueByteLength = 4 * 1024 * 1024;
        private const int PingTimeout = 90 * 1000;

        private readonly bool useSendQueue;
        private readonly bool usePingTimer;


        private readonly ConcurrentQueue<MessageQueueElement> messageQueue;
        private readonly SemaphoreSlim messageQueueSemaphore;
        private readonly SemaphoreSlim messageQueueSendTaskSemaphore;
        private int messageQueueStringLength = 0; // accumulated string length of queued messages
        private Task messageQueueSendTask;
        

        private readonly SemaphoreSlim pingTimerSemaphore;
        private volatile bool pingTimerQuit = false;
        private Task pingTimerTask;
        
        
        public AbstractSocketEndpoint(bool useSendQueue, bool usePingTimer) {
            this.useSendQueue = useSendQueue;
            this.usePingTimer = usePingTimer;
            if (useSendQueue)
            {
                // Start a send task.
                messageQueue = new ConcurrentQueue<MessageQueueElement>();
                messageQueueSemaphore = new SemaphoreSlim(0);
                messageQueueSendTaskSemaphore = new SemaphoreSlim(0);
                messageQueueSendTask = Task.Run(async () =>
                    await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(RunSendTaskAsync));
            }
            if (usePingTimer)
            {
                // start a timer task.
                pingTimerSemaphore = new SemaphoreSlim(0);
                pingTimerTask = Task.Run(async () =>
                    await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(RunPingTimerTaskAsync));
            }
        }

        public abstract Task InitializeAsync();

        /// <summary>
        /// Sends the given message to the client. This method does not block because it adds the message to
        /// a internal queue, from which a sender thread takes the messages and calls SendMessageInternalAsync().
        /// This method can therefore be called from multiple threads at the same time.
        /// 
        /// This is to avoid one client blocking all other clients when it does not read data and the send method
        /// uses locking instead of a separate task and queue.
        /// 
        /// If the client does not read data and the queued data exceeds a specific amount,
        /// the connection is aborted.
        /// 
        /// This method can throw exceptions if the connection has been aborted.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="callback">A callback that will be called shortly before the message is actually sent,
        /// (it is the latest moment before the remote client might receive the message).
        /// The Callback must not throw an exception, otherwise the process will crash due to an
        /// unhandled exception.</param>
        /// <returns></returns>
        public void SendMessageByQueue(string message, Action callback = null)
        {
            SendMessageByQueue(TextMessageEncoding.GetBytes(message), true, callback);
        }

        public void SendMessageByQueue(byte[] message, Action callback = null)
        {
            SendMessageByQueue(message, false, null);
        }

        private void SendMessageByQueue(byte[] message, bool textMessage, Action callback)
        {
            if (!useSendQueue)
                throw new InvalidOperationException("Only blocking writes are supported.");

            if (message == null)
                throw new ArgumentNullException();

            // If the size of queued messages is not too large, add the message to the queue.
            int newQueueStrLength = Interlocked.Add(ref messageQueueStringLength, message.Length);
            if (newQueueStrLength > MaxClientMessageQueueByteLength)
            {
                // The queue is too large. Abort the connection; and ensure the SendTask does not send
                // anything after the last message (in case the queue is emptied in the meanwhile and
                // this method is called again)
                messageQueue.Enqueue(new MessageQueueElement()
                {
                    EnterFaultedState = true
                });
                messageQueueSemaphore.Release();

                Abort();
                return;
            }

            // Enqueue the message.
            messageQueue.Enqueue(new MessageQueueElement()
            {
                Message = message,
                TextMessage = textMessage,
                Callback = callback
            });
            messageQueueSemaphore.Release();

        }

        /// <summary>
        /// Sends a message asynchronously to the client. The task finishes when the message
        /// has been sent completely to the client.
        /// This method can only be called if useSendQueue is false.
        /// </summary>
        /// <param name="message"></param>
        public Task SendMessageAsync(string message)
        {
            return SendMessageAsync(TextMessageEncoding.GetBytes(message), true);
        }

        public Task SendMessageAsync(byte[] message)
        {
            return SendMessageAsync(message, false);
        }

        private async Task SendMessageAsync(byte[] message, bool textMessage)
        {
            if (useSendQueue)
                throw new InvalidOperationException("Only non-blocking writes are supported.");

            await SendMessageInternalAsync(message, textMessage);
        }


        private async Task RunSendTaskAsync()
        {
            try
            {
                bool isFaulted = false;
                while (true)
                {
                    await messageQueueSemaphore.WaitAsync();
                    MessageQueueElement el;
                    if (!messageQueue.TryDequeue(out el))
                        throw new InvalidOperationException(); // should never occur

                    if (el.CloseConnection)
                    {
                        try
                        {
                            await CloseInternalAsync();
                        }
                        catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                        {
                            // Ignore
                        }
                        isFaulted = true;
                    }
                    if (el.EnterFaultedState)
                    {
                        isFaulted = true;
                    }
                    if (el.QueueEndElement)
                    {
                        return;
                    }

                    if (!isFaulted && el.Message != null)
                    {
                        // Call the callback before sending the message.
                        el.Callback?.Invoke();

                        Interlocked.Add(ref messageQueueStringLength, -el.Message.Length);

                        try
                        {
                            await SendMessageInternalAsync(el.Message, el.TextMessage);
                        }
                        catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                        {
                            // The connection is probably already closed/aborted.
                            System.Diagnostics.Debug.WriteLine(ex.ToString());
                            // Note: Don't return here as that will dispose the messageQueueSemaphore, which
                            // will then cause the code in OnHandleClose() to throw a exception as it will
                            // expect that the semaphore is still active and try to release it. Also the queue
                            // might overflow. Instead, we ignore further writes so that the SendTask continues
                            // until a QueueEndElement is received.
                            isFaulted = true;
                        }
                    }
                }
            }
            finally
            {
                messageQueueSendTaskSemaphore.Release();
                messageQueueSemaphore.Dispose();
            }
        }

        private async Task RunPingTimerTaskAsync()
        {
            while (true)
            {
                bool ok = await pingTimerSemaphore.WaitAsync(PingTimeout);

                if (pingTimerQuit)
                {
                    break;
                }
                
                if (!ok)
                {
                    // Client has run into a time-out; therefore we need to abort the connection.
                    Abort();
                    break;
                }
            }
        }

        /// <summary>
        /// Subclasses overwrite this method to handle sending a message.
        /// This method can throw an exception if the underlying I/O operation fails.
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        protected abstract Task SendMessageInternalAsync(byte[] message, bool textMessage);

        /// <summary>
        /// Closes the connection. This will block until the close frame has been sent.
        /// Note that after calling this method, the Websocket connection can still receive data until the
        /// other endpoint completes the close handshake.
        /// However, currently we don't allow the connection to be closed by the server, other than aborting it.
        /// </summary>
        /// <returns></returns>
        protected abstract Task CloseInternalAsync();

        /// <summary>
        /// Asynchronously receives the next packet. Note that the returned ReceivedPacket may
        /// use the receive buffer as rawBytes, you should should either copy those bytes or use
        /// ByteMessage/StringMessage, before the next call to this method.
        /// </summary>
        /// <param name="maxLength">The maximum packet length.</param>
        /// <returns>the next packet or null of the client closed the connection</returns>
        /// <exception cref="Exception">If an I/O error occurs</exception>
        public abstract Task<ReceivedPacket> ReceiveNextPacketAsync(int maxLength);

        /// <summary>
        /// Aborts the connection immediately. After calling this method, Read and Write calls will throw
        /// an exception.
        /// Note: This method should not throw an exception.
        /// </summary>
        public abstract void Abort();

        /// <summary>
        /// Resets the ping timer after receiving a client's PING message.
        /// </summary>
        public void HandlePing()
        {
            if (!usePingTimer)
                return;

            // Only release the semaphore if the ping timer already handled all previous
            // releases.
            if (pingTimerSemaphore.CurrentCount == 0)
                pingTimerSemaphore.Release();
        }


        /// <summary>
        /// Runs this endpoint asynchronously
        /// </summary>
        /// <param name="runFunc"></param>
        /// <returns></returns>
        public async Task RunEndpointAsync(Func<Task> runFunc)
        {
            bool isNormalClose = true;
            try
            {
                await RunEndpointInternalAsync(runFunc);
            }
            catch
            {
                // Abort the connection when a exception occured.
                Abort();
                isNormalClose = false;
                throw;
            }
            finally
            {
                await HandleCloseAsync(isNormalClose);
            }
        }

        protected virtual async Task RunEndpointInternalAsync(Func<Task> runFunc)
            => await runFunc();


        protected async Task HandleCloseAsync(bool closeConnection)
        {
            if (closeConnection && !useSendQueue)
            {
                // Try to close the connection normally.
                try
                {
                    await CloseInternalAsync();
                }
                catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                {
                    // Ignore
                }
            }
            if (useSendQueue)
            {
                // Stop the send task.
                messageQueue.Enqueue(new MessageQueueElement()
                {
                    QueueEndElement = true,
                    CloseConnection = closeConnection
                });
                messageQueueSemaphore.Release();

                // Wait for the send task to complete. We wait asynchronously because it could
                // take some time (it it takes too long, the ping timer will still be able to abort
                // the connection).
                await messageQueueSendTaskSemaphore.WaitAsync();
                messageQueueSendTaskSemaphore.Dispose();
                // Now wait synchronously for the task to end (which will happen immediatly)
                // so we can get exceptions.
                messageQueueSendTask.Wait();
                messageQueueSendTask.Dispose();
            }

            if (usePingTimer)
            {
                pingTimerQuit = true;
                pingTimerSemaphore.Release();

                pingTimerTask.Wait();
                pingTimerTask.Dispose();
                pingTimerSemaphore.Dispose();
            }
        }


        private struct MessageQueueElement
        {
            public Action Callback;
            public byte[] Message;
            public bool TextMessage;
            public bool QueueEndElement;
            public bool CloseConnection;
            public bool EnterFaultedState;
        }

        
    }

    internal class ReceivedPacket
    {
        private ArraySegment<byte> rawBytes;
        private byte[] convertedBytes = null;
        private string convertedString = null;
        private ReceivedPacketType type;

        public ReceivedPacketType Type => type;

        /// <summary>
        /// Returns the raw byte segment. Note (esp. for the TcpClientEndpoint) that
        /// this buffer may change when reading the next packet.
        /// </summary>
        public ArraySegment<byte> RawBytes
        {
            get
            {
                return rawBytes;
            }
        }

        public byte[] ByteMessage
        {
            get
            {
                if (convertedBytes == null)
                {
                    convertedBytes = new byte[rawBytes.Count];
                    Array.Copy(rawBytes.Array, rawBytes.Offset, convertedBytes, 0, convertedBytes.Length);
                }
                return convertedBytes;
            }
        }
        public string StringMessage => convertedString ??
            (convertedString = Encoding.UTF8.GetString(rawBytes.Array, rawBytes.Offset, rawBytes.Count));

        public ReceivedPacket(ArraySegment<byte> rawBytes, ReceivedPacketType type)
        {
            this.rawBytes = rawBytes;
            this.type = type;
        }
    }

    internal enum ReceivedPacketType : int
    {
        /// <summary>
        /// Specifies that the packet type is unknown. This also means the packet is received
        /// from a stream, so it is not a split (e.g. if received from a raw TCP connection).
        /// </summary>
        Unknown = 0,
        /// <summary>
        /// The length of the frame is known and it is a byte message.
        /// </summary>
        ByteMessage = 1,
        /// <summary>
        /// The length of the frame is known and it is a string message.
        /// </summary>
        StringMessage = 2
    }

}
