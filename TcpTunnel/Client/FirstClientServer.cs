using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using SimpleSocketClient;

using TcpTunnel.Utils;

namespace TcpTunnel.Client;

internal class FirstClientServer
{
    private readonly IReadOnlyList<TcpTunnelConnectionDescriptor> connectionDescriptors;
    private readonly Action<long, TcpClient, TcpTunnelConnectionDescriptor> clientAcceptor;

    private readonly List<(TcpListener listener, Task task)> firstClientListeners = new();
    private readonly object syncRoot = new();

    private long nextConnectionId;
    private bool stopped;

    public FirstClientServer(
        IReadOnlyList<TcpTunnelConnectionDescriptor> connectionDescriptors,
        Action<long, TcpClient, TcpTunnelConnectionDescriptor> clientAcceptor)
    {
        this.connectionDescriptors = connectionDescriptors;
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
                if (descriptor.ListenIP is null)
                    listener = TcpListener.Create(descriptor.ListenPort);
                else
                    listener = new TcpListener(descriptor.ListenIP, descriptor.ListenPort);

                listener.Start();
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                // Ignore.
                // TODO: Log.
                continue;
            }

            var listenerTask = ExceptionUtils.StartTask(
                () => this.RunListenerTask(listener, descriptor));

            this.firstClientListeners.Add((listener, listenerTask));
        }
    }

    public async ValueTask StopAsync()
    {
        Volatile.Write(ref this.stopped, true);

        foreach (var tuple in this.firstClientListeners)
        {
            tuple.listener.Stop();
            await tuple.task;
        }
    }

    private async Task RunListenerTask(
        TcpListener listener,
        TcpTunnelConnectionDescriptor connectionDescriptor)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex.CanCatch())
            {
                // Check if the error occured because we need to stop.
                if (Volatile.Read(ref this.stopped))
                    break;

                // It is another error, so ignore it. This can sometimes happen when the
                // client closed the connection directly after accepting it.
                continue;
            }

            // After the socket is connected, configure it to disable the Nagle
            // algorithm and delayed ACKs (and maybe enable TCP keep-alive in the
            // future).
            SocketConfigurator.ConfigureSocket(client.Client);

            // Handle the client.
            long newConnectionId;
            lock (this.syncRoot)
            {
                newConnectionId = checked(this.nextConnectionId++);
            }

            this.clientAcceptor(newConnectionId, client, connectionDescriptor);
        }
    }
}
