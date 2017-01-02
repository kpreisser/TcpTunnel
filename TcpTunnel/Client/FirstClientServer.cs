using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.SocketInterfaces;
using TcpTunnel.Utils;

namespace TcpTunnel.Client
{
    internal class FirstClientServer
    {
        private readonly IReadOnlyList<TcpTunnelConnectionDescriptor> connectionDescriptors;
        private readonly TcpClientFramingEndpoint endpoint;
        private readonly Action<long, TcpClient, TcpTunnelConnectionDescriptor> clientAcceptor;


        private List<Tuple<TcpListener, Task>> firstClientListeners = new List<Tuple<TcpListener, Task>>();

        private bool stopped;

        public FirstClientServer(IReadOnlyList<TcpTunnelConnectionDescriptor> connectionDescriptors,
            TcpClientFramingEndpoint endpoint,
            Action<long, TcpClient, TcpTunnelConnectionDescriptor> clientAcceptor)
        {
            this.connectionDescriptors = connectionDescriptors;
            this.endpoint = endpoint;
            this.clientAcceptor = clientAcceptor;
        }

        public void Start()
        {
            // Create listeners.
            foreach (var descriptor in this.connectionDescriptors)
            {
                TcpListener listener;
                try
                {
                    if (descriptor.ListenIP == null)
                        listener = TcpListener.Create(descriptor.ListenPort);
                    else
                        listener = new TcpListener(descriptor.ListenIP, descriptor.ListenPort);
                    listener.Start();
                }
                catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                {
                    // Ignore.
                    Debug.WriteLine(ex.ToString());
                    continue;
                }

                var listenerTask = Task.Run(async () => await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(
                    async () => await RunListenerTask(listener, descriptor)));

                this.firstClientListeners.Add(new Tuple<TcpListener, Task>(listener, listenerTask));
            }
        }

        public void Stop()
        {
            Volatile.Write(ref stopped, true);
            foreach (var tuple in this.firstClientListeners)
            {
                tuple.Item1.Stop();
                tuple.Item2.Wait();
                tuple.Item2.Dispose();
            }
        }

        private async Task RunListenerTask(TcpListener listener, TcpTunnelConnectionDescriptor portAndRemoteHost)
        {
            long currentConnectionId = 0;

            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                    client.NoDelay = Constants.TcpClientNoDelay;
                }
                catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                {
                    // Can be a SocketException or an ObjectDisposedException.
                    // Need to break out of the loop.
                    // However, if stopped is not set to true, it is another error so rethrow it.
                    if (!Volatile.Read(ref stopped))
                        throw;

                    return;
                }

                // Handle the client.
                long newConnectionID = checked(currentConnectionId++);
                this.clientAcceptor(newConnectionID, client, portAndRemoteHost);
            }
        }
    }
}
