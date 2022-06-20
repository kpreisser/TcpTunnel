using System;
using System.Collections.Generic;
using System.Globalization;
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
            var listener = default(TcpListener);
            try
            {
                if (descriptor.ListenIP is null)
                    listener = TcpListener.Create(descriptor.ListenPort);
                else
                    listener = new TcpListener(descriptor.ListenIP, descriptor.ListenPort);

                listener.Start();
            }
            catch (Exception ex)
            {
                // Stop() will dispose the underlying socket.
                try
                {
                    listener?.Stop();
                }
                catch
                {
                    // Ignore.
                }

                // Stop the previously started listeners, then rethrow the exception.
                this.Stop();

                string hostPort = (descriptor.ListenIP?.ToString() ?? "<any>") + ":" +
                    descriptor.ListenPort.ToString(CultureInfo.InvariantCulture);

                throw new InvalidOperationException(
                    $"Could not listen on '{hostPort}': {ex.Message}",
                    ex);
            }

            var listenerTask = ExceptionUtils.StartTask(
                () => this.RunListenerTask(listener, descriptor));

            this.listeners.Add((listener, listenerTask));
        }
    }

    public void Stop()
    {
        Volatile.Write(ref this.stopped, true);

        foreach (var (listener, task) in this.listeners)
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Ignore.
            }

            // Wait for the listener task to finish.
            task.GetAwaiter().GetResult();
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
            catch
            {
                // Check if the error occured because we need to stop.
                if (Volatile.Read(ref this.stopped))
                    break;

                // It is another error, so try again. This can happen when the
                // connection was reset while it was in the backlog.
                continue;
            }

            // After the socket is connected, configure it to disable the Nagle
            // algorithm, disable delayed ACKs, and enable TCP keep-alive.
            SocketConfigurator.ConfigureSocket(client.Client, enableKeepAlive: true);

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
