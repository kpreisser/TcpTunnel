using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SimpleSocketClient;

using TcpTunnel.Networking;
using TcpTunnel.Utils;

namespace TcpTunnel.Gateway;

public class Gateway : IInstance
{
    // The max message size is defined by the receive buffer size (32 KiB) plus
    // the additional data, which are just a few bytes. Therefore, 512 KiB should
    // be more than enough.
    public const int MaxReceiveMessageSize = 512 * 1024;

    private readonly IReadOnlyList<(IPAddress? ip, int port, X509Certificate2? certificate)> listenEntries;

    private readonly List<(TcpListener listener, Task task)> activeListeners = new();

    private readonly Action<string>? logger;

    private bool stopped;

    public Gateway(
        IReadOnlyList<(IPAddress? ip, int port, X509Certificate2? certificate)> listenEntries,
        IReadOnlyDictionary<int, (string proxyClientPassword, string proxyServerPassword)> sessions,
        Action<string>? logger = null)
    {
        this.listenEntries = listenEntries;
        this.logger = logger;

        this.SyncRoot = new();

        var sessionDictionary = new Dictionary<int, Session>();
        this.Sessions = sessionDictionary;

        foreach (var pair in sessions)
        {
            sessionDictionary.Add(
                pair.Key,
                new Session(
                    Encoding.UTF8.GetBytes(pair.Value.proxyClientPassword),
                    Encoding.UTF8.GetBytes(pair.Value.proxyServerPassword)));
        }
    }

    internal IReadOnlyDictionary<int, Session> Sessions
    {
        get;
    }

    internal object SyncRoot
    {
        get;
    }

    internal Action<string>? Logger
    {
        get => this.logger;
    }

    public void Start()
    {
        Volatile.Write(ref this.stopped, false);

        foreach (var (ip, port, certificate) in this.listenEntries)
        {
            var listener = default(TcpListener);
            try
            {
                if (ip is null)
                    listener = TcpListener.Create(port);
                else
                    listener = new TcpListener(ip, port);

                listener.Start();
            }
            catch
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
                throw;
            }

            this.logger?.Invoke(
                $"Gateway listener started on '{ip?.ToString() ?? "<any>"}:{port.ToString(CultureInfo.InvariantCulture)}'.");

            var listenerTask = ExceptionUtils.StartTask(
                () => this.RunListenerTask(listener, certificate));

            this.activeListeners.Add((listener, listenerTask));
        }
    }

    public void Stop()
    {
        Volatile.Write(ref this.stopped, true);

        foreach (var (listener, listenerTask) in this.activeListeners)
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
            listenerTask.GetAwaiter().GetResult();
        }

        this.activeListeners.Clear();
    }

    private async Task RunListenerTask(TcpListener listener, X509Certificate2? certificate)
    {
        bool isStopped = false;
        var streamModifier = ModifyStreamAsync;

        var activeHandlers = new Dictionary<GatewayProxyConnectionHandler, (Task task, bool canCancelEndpoint)>();

        try
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

                var remoteEndpoint = client.Client.RemoteEndPoint!;
                this.logger?.Invoke($"Accepted connection from '{remoteEndpoint}'.");

                var handler = default(GatewayProxyConnectionHandler);
                var endpoint = new TcpClientFramingEndpoint(
                    client,
                    useSendQueue: true,
                    usePingTimer: true,
                    connectHandler: cancellationToken =>
                    {
                        // Beginning from this stage, the endpoint can be
                        // canceled.
                        lock (activeHandlers)
                        {
                            if (isStopped)
                                throw new OperationCanceledException();

                            CollectionsMarshal.GetValueRefOrNullRef(activeHandlers, handler!)
                                .canCancelEndpoint = true;
                        }

                        return default;
                    },
                    closeHandler: () =>
                    {
                        // Once this handler returns, the endpoint may no longer
                        // be canceled.
                        lock (activeHandlers)
                        {
                            CollectionsMarshal.GetValueRefOrNullRef(activeHandlers, handler!)
                                .canCancelEndpoint = false;
                        }
                    },
                    streamModifier: streamModifier);

                handler = new GatewayProxyConnectionHandler(this, endpoint, remoteEndpoint);

                lock (activeHandlers)
                {
                    // Start a task to handle the endpoint.
                    var runTask = ExceptionUtils.StartTask(async () =>
                    {
                        try
                        {
                            await endpoint.RunEndpointAsync(handler.RunAsync);
                        }
                        catch
                        {
                            // Ignore.
                        }
                        finally
                        {
                            try
                            {
                                client.Dispose();
                            }
                            catch
                            {
                                // Ignore.
                            }

                            lock (activeHandlers)
                            {
                                // Remove the handler.
                                activeHandlers.Remove(handler);
                            }

                            this.logger?.Invoke($"Closed connection from '{remoteEndpoint}'.");
                        }
                    });

                    activeHandlers.Add(handler, (runTask, false));
                }
            }
        }
        finally
        {
            // Stop all active connections.
            // Need to add the tasks in a separate list, because we cannot wait for them
            // while we hold the lock for activeHandlers, otherwise a deadlock might occur
            // because the handlers also remove themselves from that dictionary.
            var tasksToWaitFor = new List<Task>();
            lock (activeHandlers)
            {
                isStopped = true;

                foreach (var cl in activeHandlers)
                {
                    if (cl.Value.canCancelEndpoint)
                        cl.Key.Endpoint.Cancel();

                    tasksToWaitFor.Add(cl.Value.task);
                }
            }

            // After releasing the lock, wait for the tasks.
            // Note: The tasks remove themselves from the dictionary, so a task might still
            // be executing  although it is not in the dictionary any more.
            // However this is OK because the task doesn't do anything after that point.
            foreach (var t in tasksToWaitFor)
            {
                await t;
            }
        }

        async ValueTask<Stream?> ModifyStreamAsync(
            NetworkStream networkStream,
            CancellationToken cancellationToken)
        {
            if (certificate is not null)
            {
                var sslStream = new SslStream(networkStream);
                try
                {
                    await sslStream.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions()
                        {
                            // AllowRenegotiation is still true by default up to .NET 6.0.
                            AllowRenegotiation = false,
                            ServerCertificate = certificate,
                            EnabledSslProtocols = Constants.sslProtocols
                        },
                        cancellationToken);
                }
                catch
                {
                    await sslStream.DisposeAsync();
                    throw;
                }

                return sslStream;
            }

            return null;
        }
    }

    public void Dispose()
    {
        this.Stop();
    }
}

