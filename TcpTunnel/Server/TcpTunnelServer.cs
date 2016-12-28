using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.SocketInterfaces;
using TcpTunnel.Utils;

namespace TcpTunnel.Server
{
    internal class TcpTunnelServer
    {
        public const int MaxSendBufferSize = 4 * 1024 * 1024;

        private readonly int port;
        private readonly X509Certificate2 certificate;
        
        private TcpListener listener;
        private Task listenerTask;
        private List<SocketHandlerWrapper> activeHandlers = new List<SocketHandlerWrapper>();
        private bool stopped = false;

        internal SortedDictionary<int, Session> sessions { get; } =
            new SortedDictionary<int, Session>();

        internal object SyncRoot { get; } = new object();

        public TcpTunnelServer(int port, X509Certificate2 certificate, IDictionary<int, string> endpoints)
        {
            this.port = port;
            this.certificate = certificate;
            this.sessions = new SortedDictionary<int, Session>();
            foreach (var pair in endpoints)
                this.sessions.Add(pair.Key, new Session(pair.Value));

            this.listener = TcpListener.Create(port);
        }

        public void Start()
        {
            listener.Start();
            listenerTask = Task.Run(async () =>
                await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(RunListenerTask));
        }
        
        public void Stop()
        {
            Volatile.Write(ref stopped, true);
            listener.Stop();

            // Wait for the listener task.
            listenerTask.Wait();
            listenerTask.Dispose();
        }

        private async Task RunListenerTask()
        {
            try
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                    {
                        // Can be a SocketException or an ObjectDisposedException.
                        // Need to break out of the loop.
                        // However, if stopped is not set to true, it is another error so rethrow it.
                        if (!Volatile.Read(ref stopped))
                            throw;

                        break;
                    }

                    // Disable Nagle altorithm because we need frames to be sent to the client as
                    // soon as possible.
                    //client.NoDelay = true;

                    var endpoint = new TcpClientFramingEndpoint(client, true, true, ModifyStream);
                    ConnectionHandler handler = new ConnectionHandler(this, endpoint);
                    // Creating the wrapper and adding it to the dictionary needs to be
                    // done before actually starting the task to avoid a race.
                    SocketHandlerWrapper wrp = new SocketHandlerWrapper()
                    {
                         client = endpoint
                    };
                    lock (activeHandlers)
                    {
                        activeHandlers.Add(wrp);
                    }

                    // Start a task to handle the endpoint.
                    Task runTask = Task.Run(async () =>
                        await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(async () =>
                        {
                            try
                            {
                                await endpoint.RunEndpointAsync(handler.RunAsync);
                            }
                            catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                            {
                                // Ignore.
                                System.Diagnostics.Debug.WriteLine(ex.GetType() + ": " + ex.Message);
                            }
                            finally
                            {
                                lock (activeHandlers)
                                {
                                    // Remove the handler.
                                    activeHandlers.Remove(wrp);
                                }
                            }
                        }));
                    wrp.handlerTask = runTask;
                }
            }
            finally
            {
                // Stop all active clients.
                // Need to add the tasks in a separate list, because we cannot wait for them while we
                // hold the lock for activeHandlers, otherwise a deadlock might occur because the handlers
                // also remove themselves from that dictionary.
                List<Task> tasksToWaitFor = new List<Task>();
                lock (activeHandlers)
                {
                    foreach (SocketHandlerWrapper cl in activeHandlers)
                    {
                        cl.client.Abort();
                        tasksToWaitFor.Add(cl.handlerTask);
                    }
                }

                // After releasing the lock, wait for the tasks.
                // Note: This is not quite clean because as the tasks remove themselves from the dictionary,
                // a task might still be active although it is not in the dictionary any more.
                // However this is OK because the task doesn't do anything after that point.
                foreach (Task t in tasksToWaitFor)
                {
                    t.Wait();
                    t.Dispose();
                }
            }
        }

        private async Task<Stream> ModifyStream(NetworkStream s)
        {
            if (this.certificate == null)
            {
                return s;
            }
            else
            {
                var ssl = new SslStream(s);
                await ssl.AuthenticateAsServerAsync(this.certificate, false, System.Security.Authentication.SslProtocols.Tls12, false);
                return ssl;
            }
        }

        public void Dispose()
        {
            Stop();
        }



        private class SocketHandlerWrapper
        {
            public TcpClientEndpoint client;
            public Task handlerTask;
        }
    }
}

