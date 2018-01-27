using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.SocketInterfaces;
using TcpTunnel.Utils;

namespace TcpTunnel.Server
{
    public class TcpTunnelServer
    {
        public const int MaxReceivePacketSize = 2 * 1024 * 1024;
        public const int MaxSendBufferSize = 5 * 1024 * 1024;

        private readonly int port;
        private readonly X509Certificate2 certificate;
        
        private TcpListener listener;
        private Task listenerTask;
        private List<SocketHandlerWrapper> activeHandlers = new List<SocketHandlerWrapper>();
        private bool stopped = false;

        internal SortedDictionary<int, Session> sessions { get; } =
            new SortedDictionary<int, Session>();

        internal object SyncRoot { get; } = new object();


        public TcpTunnelServer(int port, X509Certificate2 certificate, IDictionary<int, string> sessions)
        {
            this.port = port;
            this.certificate = certificate;
            this.sessions = new SortedDictionary<int, Session>();
            foreach (var pair in sessions)
                this.sessions.Add(pair.Key, new Session(pair.Value));

            this.listener = TcpListener.Create(port);
        }


        public void Start()
        {
            this.listener.Start();
            this.listenerTask = Task.Run(async () =>
                await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(RunListenerTask));
        }
        
        public void Stop()
        {
            Volatile.Write(ref this.stopped, true);
            this.listener.Stop();

            // Wait for the listener task.
            this.listenerTask.Wait();
            this.listenerTask.Dispose();
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
                        client = await this.listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                    {
                        // Check if the error occured because we need to stop.
                        if (Volatile.Read(ref this.stopped))
                            break;

                        // It is another error, so ignore it. This can sometimes happen when the
                        // client closed the connection directly after accepting it.
                        continue;
                    }

                    client.NoDelay = Constants.TcpClientNoDelay;

                    var endpoint = new TcpClientFramingEndpoint(client, true, true, ModifyStreamAsync);
                    var handler = new ConnectionHandler(this, endpoint);
                    // Creating the wrapper and adding it to the dictionary needs to be
                    // done before actually starting the task to avoid a race.
                    var wrp = new SocketHandlerWrapper()
                    {
                         client = endpoint
                    };
                    lock (this.activeHandlers)
                    {
                        this.activeHandlers.Add(wrp);
                    }

                    // Start a task to handle the endpoint.
                    var runTask = Task.Run(async () =>
                        await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(async () =>
                        {
                            try
                            {                                
                                await endpoint.RunEndpointAsync(async () =>
                                {
                                    await endpoint.InitializeAsync();
                                    await handler.RunAsync();
                                });
                            }
                            catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                            {
                                // Ignore.
                                System.Diagnostics.Debug.WriteLine(ex.GetType() + ": " + ex.Message);
                            }
                            finally
                            {
                                lock (this.activeHandlers)
                                {
                                    // Remove the handler.
                                    this.activeHandlers.Remove(wrp);
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
                var tasksToWaitFor = new List<Task>();
                lock (this.activeHandlers)
                {
                    foreach (var cl in this.activeHandlers)
                    {
                        cl.client.Abort();
                        tasksToWaitFor.Add(cl.handlerTask);
                    }
                }

                // After releasing the lock, wait for the tasks.
                // Note: This is not quite clean because as the tasks remove themselves from the dictionary,
                // a task might still be active although it is not in the dictionary any more.
                // However this is OK because the task doesn't do anything after that point.
                foreach (var t in tasksToWaitFor)
                {
                    t.Wait();
                    t.Dispose();
                }
            }
        }

        private async Task<Tuple<TcpClient, Stream>> ModifyStreamAsync(NetworkStream s)
        {
            if (this.certificate == null)
            {
                return new Tuple<TcpClient, Stream>(null, null);
            }
            else
            {
                var ssl = new SslStream(s);
                await ssl.AuthenticateAsServerAsync(this.certificate, false, Constants.sslProtocols, false);
                return new Tuple<TcpClient, Stream>(null, ssl);
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

