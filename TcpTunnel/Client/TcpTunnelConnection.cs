using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.Utils;

namespace TcpTunnel.Client
{
    internal class TcpTunnelConnection
    {
        private readonly TcpClient remoteClient;
        private NetworkStream remoteClientStream;
        // can be null if we don't need to connect
        private readonly Func<Task> connectHandler;
        private readonly Action<ArraySegment<byte>> receiveHandler;
        private readonly Action<int> transmitWindowUpdateHandler;

        private readonly object syncRoot = new object();
        private ConcurrentQueue<TransmitPacketQueueEntry> transmitPacketQueue;
        private SemaphoreSlim transmitPacketQueueSemaphore;

        /// <summary>
        /// A queue which contains at most 2 entries. The sum of all entries is the remaining receive window.
        /// The receive task only starts to receive if the window is at least 1/8 of the default window size.
        /// </summary>
        private ConcurrentQueue<ReceiveWindowQueueEntry> receiveWindowQueue;
        private SemaphoreSlim receiveWindowQueueSemaphore;

        private Task receiveTask;
        private bool receiveTaskStopped;
        private bool transmitTaskStopped;

        public TcpTunnelConnection(TcpClient remoteClient, Func<Task> connectHandler,
            Action<ArraySegment<byte>> receiveHandler, Action<int> transmitWindowUpdateHandler)
        {
            this.remoteClient = remoteClient;
            this.connectHandler = connectHandler;
            this.receiveHandler = receiveHandler;
            this.transmitWindowUpdateHandler = transmitWindowUpdateHandler;

            this.transmitPacketQueue = new ConcurrentQueue<TransmitPacketQueueEntry>();
            this.transmitPacketQueueSemaphore = new SemaphoreSlim(0);
            this.receiveWindowQueue = new ConcurrentQueue<ReceiveWindowQueueEntry>();
            this.receiveWindowQueueSemaphore = new SemaphoreSlim(0);
        }

        public void Start()
        {
            // Create the receive task (the transmit task will be created by the send task).
            this.receiveTask = Task.Run(async () => await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(
                RunReceiveTaskAsync));
        }


        public void Stop()
        {
            lock (this.syncRoot)
            {
                remoteClient.Close();

                if (!receiveTaskStopped)
                {
                    // Allow the receiver task to continue if it is currently blocked waiting for more window.
                    receiveWindowQueue.Enqueue(new ReceiveWindowQueueEntry()
                    {
                        Exit = true
                    });
                    receiveWindowQueueSemaphore.Release();
                }
            }

            this.receiveTask.Wait();
        }

        public void EnqueuePacket(ArraySegment<byte> packet)
        {
            // Packet's count can be 0 to shutdown the connection after sending all enqueued packets.
            lock (this.syncRoot)
            {
                if (!transmitTaskStopped)
                {
                    transmitPacketQueue.Enqueue(new TransmitPacketQueueEntry()
                    {
                        Packet = packet
                    });
                    transmitPacketQueueSemaphore.Release();
                }
            }
        }

        public void UpdateReceiveWindow(int newWindow)
        {
            lock (this.syncRoot)
            {
                if (!this.receiveTaskStopped)
                {
                    int window = 0;
                    bool exit = false;

                    while (receiveWindowQueueSemaphore.Wait(0))
                    {
                        ReceiveWindowQueueEntry e;
                        if (!receiveWindowQueue.TryDequeue(out e))
                            throw new InvalidOperationException();

                        window += e.Window;
                        exit |= e.Exit;
                    }

                    window += newWindow;

                    receiveWindowQueue.Enqueue(new ReceiveWindowQueueEntry()
                    {
                        Window = window,
                        Exit = exit
                    });
                    receiveWindowQueueSemaphore.Release();
                }
            }
        }


        private async Task RunReceiveTaskAsync()
        {
            Task transmitTask = null;
            try
            {
                if (connectHandler != null)
                    await connectHandler();

                // After connecting, create the transmit task.
                this.remoteClientStream = this.remoteClient.GetStream();

                transmitTask = Task.Run(async () => await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(
                    RunTransmitTaskAsync));

                // Update the transmit window.
                transmitWindowUpdateHandler?.Invoke(Constants.InitialWindowSize);

                // Now start to receive.
                byte[] receiveBuffer = new byte[Constants.ReceiveBufferSize];
                int availableWindow = 0;
                while (true)
                {
                    bool wait = false;
                    while (true)
                    {
                        // Collect the available window.
                        bool exit = false;
                        for (int i = 0; ; i++)
                        {
                            ReceiveWindowQueueEntry e;
                            if (i == 0 && wait)
                                await receiveWindowQueueSemaphore.WaitAsync();
                            else if (!receiveWindowQueueSemaphore.Wait(0))
                                break;
                            if (!receiveWindowQueue.TryDequeue(out e))
                                throw new InvalidOperationException();

                            checked
                            {
                                availableWindow += e.Window;
                            }
                            exit |= e.Exit;
                        }

                        if (exit)
                            return;

                        if (availableWindow >= Constants.InitialWindowSize / 8)
                            break;

                        // Insufficient window is available, so we need to wait.
                        wait = true;
                    }

                    int received = await remoteClientStream.ReadAsync(receiveBuffer, 0,
                        Math.Min(availableWindow, receiveBuffer.Length));
                    if (received == 0)
                        return; // Connection has been closed.

                    availableWindow -= received;

                    // Create a new byte array because the receive handler might need to access it later.
                    byte[] receivedArray = new byte[received];
                    Array.Copy(receiveBuffer, receivedArray, received);
                    receiveHandler?.Invoke(new ArraySegment<byte>(receivedArray));
                }
            }
            catch (Exception ex) when (ExceptionUtils.FilterException(ex))
            {
                // Ignore.
                Debug.WriteLine(ex.ToString());
            }
            finally
            {
                // We completely close the socket after we received a shutdown, to simplify the management.
                lock (this.syncRoot)
                {
                    if (!transmitTaskStopped)
                    {
                        transmitPacketQueue.Enqueue(new TransmitPacketQueueEntry());
                        transmitPacketQueueSemaphore.Release();
                    }

                    receiveTaskStopped = true;
                    receiveWindowQueueSemaphore.Dispose();

                    remoteClient.Close();
                }

                if (transmitTask != null)
                {
                    transmitTask.Wait();
                    transmitTask.Dispose();
                }
                transmitPacketQueueSemaphore.Dispose();

                // Invoke the receive handler with an empty array. We do this after waiting for the transmit task,
                // to ensure the transmitWindowUpdateHandler is no more called at this point.
                receiveHandler?.Invoke(new ArraySegment<byte>(new byte[0]));
            }
        }

        private async Task RunTransmitTaskAsync()
        {
            try
            {
                while (true)
                {
                    await transmitPacketQueueSemaphore.WaitAsync();
                    TransmitPacketQueueEntry packet;
                    if (!transmitPacketQueue.TryDequeue(out packet))
                        throw new InvalidOperationException();

                    if (packet.Packet.Count == 0)
                    {
                        // Close the connection and return.
                        lock (this.syncRoot)
                            this.remoteClient.Close();
                        return;
                    }

                    // Send the packet.
                    await remoteClientStream.WriteAsync(packet.Packet.Array, packet.Packet.Offset, packet.Packet.Count);
                    // Update the transmit window.
                    transmitWindowUpdateHandler?.Invoke(packet.Packet.Count);
                }
            }
            catch (Exception ex) when (ExceptionUtils.FilterException(ex))
            {
                // Ignore.
                Debug.WriteLine(ex.ToString());
            }
            finally
            {
                lock (this.syncRoot)
                {
                    transmitTaskStopped = true;
                }
            }
        }



        private struct TransmitPacketQueueEntry
        {
            // If packet.Array is null, shutdown the connection
            public ArraySegment<byte> Packet;
        }

        private struct ReceiveWindowQueueEntry
        {
            public int Window;
            public bool Exit;
        }
    }
}
