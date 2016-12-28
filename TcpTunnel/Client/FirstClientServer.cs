using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpTunnel.SocketInterfaces;
using TcpTunnel.Utils;

namespace TcpTunnel.Client
{
    internal class FirstClientServer
    {
        private readonly IDictionary<int, string> portsAndRemoteHostnames;
        private readonly TcpClientFramingEndpoint endpoint;
        private readonly Action<TcpClient, KeyValuePair<int, string>> clientAcceptor;


        private List<Tuple<TcpListener, Task>> firstClientListeners;

        private bool stopped;

        public FirstClientServer(IDictionary<int, string> portsAndRemoteHostnames, TcpClientFramingEndpoint endpoint,
            Action<TcpClient, KeyValuePair<int, string>> clientAcceptor)
        {
            this.portsAndRemoteHostnames = portsAndRemoteHostnames;
            this.endpoint = endpoint;
            this.clientAcceptor = clientAcceptor;
        }

        public void Start()
        {
            // Create listeners.
            foreach (var pair in this.portsAndRemoteHostnames)
            {
                TcpListener listener;
                try
                {
                    listener = TcpListener.Create(pair.Key);
                    listener.Start();
                }
                catch (Exception ex) when (ExceptionUtils.FilterException(ex))
                {
                    // Ignore.
                    Debug.WriteLine(ex.ToString());
                    continue;
                }

                var listenerTask = Task.Run(async () => await ExceptionUtils.WrapTaskForHandlingUnhandledExceptions(
                    async () => await RunListenerTask(listener, pair)));

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

        private async Task RunListenerTask(TcpListener listener, KeyValuePair<int, string> portAndRemoteHost)
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

                    return;
                }

                // Handle the client.
                this.clientAcceptor(client, portAndRemoteHost);
            }
        }
    }
}
