﻿using System;
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

    private readonly object syncRoot = new();

    private readonly List<(TcpListener listener, Task task)> listeners = new();

    private CancellationTokenSource? listenersCts;

    private long nextConnectionId;

    public ProxyServerListener(
        IReadOnlyList<ProxyServerConnectionDescriptor> connectionDescriptors,
        Action<long, TcpClient, ProxyServerConnectionDescriptor> clientAcceptor)
    {
        this.connectionDescriptors = connectionDescriptors;
        this.clientAcceptor = clientAcceptor;
    }

    public void Start()
    {
        this.listenersCts = new();
        try
        {
            // Create the listeners.
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
                    () => this.RunListenerTask(listener, descriptor, this.listenersCts.Token));

                this.listeners.Add((listener, listenerTask));
            }
        }
        catch
        {
            // The CTS may already have been disposed (and cleared out) by Stop().
            this.listenersCts?.Dispose();
            this.listenersCts = null;

            throw;
        }
    }

    public void Stop()
    {
        if (this.listenersCts is null)
            return;

        try
        {
            this.listenersCts.Cancel();
        }
        catch (AggregateException)
        {
            // Ignore.
            // This can occur with some implementations, e.g. registered callbacks
            // from  WebSocket operations using HTTP.sys (from ASP.NET Core) can
            // throw here when calling Cancel() and the IWebHost has already been
            // disposed.
        }

        foreach (var (listener, task) in this.listeners)
        {
            // Wait for the listener task to finish.
            task.GetAwaiter().GetResult();

            // Dispose of the TcpListener.
            listener.Stop();
        }

        this.listeners.Clear();
        this.listenersCts.Dispose();
        this.listenersCts = null;
    }

    private async Task RunListenerTask(
        TcpListener listener,
        ProxyServerConnectionDescriptor connectionDescriptor,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (SocketException)
            {
                // This can happen when the connection got reset while it
                // was in the backlog. In that case, just try again.
                continue;
            }
            catch (OperationCanceledException)
            {
                // The CTS was cancelled.
                break;
            }

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
