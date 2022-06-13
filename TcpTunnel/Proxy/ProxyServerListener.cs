using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using SimpleSocketClient;

using TcpTunnel.Utils;

namespace TcpTunnel.Proxy;

internal class ProxyServerListener
{
    private readonly IReadOnlyList<ProxyServerConnectionDescriptor> connectionDescriptors;
    private readonly Action<long, TcpClient, ProxyServerConnectionDescriptor> clientAcceptor;

    private readonly List<(TcpListener listener, Task task)> listeners = new();
    private readonly object syncRoot = new();

    private long nextConnectionId;
    private bool stopped;

    public ProxyServerListener(
        IReadOnlyList<ProxyServerConnectionDescriptor> connectionDescriptors,
        Action<long, TcpClient, ProxyServerConnectionDescriptor> clientAcceptor)
    {
        this.connectionDescriptors = connectionDescriptors;
        this.clientAcceptor = clientAcceptor;
    }

    public void Start()
    {
        Volatile.Write(ref this.stopped, false);

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
                // Stop the previously started listeners, then rethrow the exception.
                this.Stop();
                throw;
            }

            var listenerTask = ExceptionUtils.StartTask(
                () => this.RunListenerTask(listener, descriptor));

            this.listeners.Add((listener, listenerTask));
        }
    }

    public void Stop()
    {
        Volatile.Write(ref this.stopped, true);

        foreach (var tuple in this.listeners)
        {
            tuple.listener.Stop();
            tuple.task.GetAwaiter().GetResult();
        }

        this.listeners.Clear();
    }

    private async Task RunListenerTask(
        TcpListener listener,
        ProxyServerConnectionDescriptor connectionDescriptor)
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
