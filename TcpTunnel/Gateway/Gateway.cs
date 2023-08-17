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

    private CancellationTokenSource? listenersCts;

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
        this.listenersCts = new();
        try
        {
            // Create the listeners.
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
                    () => this.RunListenerTask(listener, certificate, this.listenersCts.Token));

                this.activeListeners.Add((listener, listenerTask));
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

        this.listenersCts.CancelAndIgnoreAggregateException();

        foreach (var (listener, task) in this.activeListeners)
        {
            // Wait for the listener task to finish.
            task.GetAwaiter().GetResult();

            // Dispose of the TcpListener.
            listener.Stop();
        }

        this.activeListeners.Clear();
        this.listenersCts.Dispose();
        this.listenersCts = null;
    }

    private async Task RunListenerTask(
        TcpListener listener,
        X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        var streamModifier = ModifyStreamAsync;

        var activeHandlers = new Dictionary<GatewayProxyConnectionHandler, Task>();

        try
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

                // After the socket is connected, configure it to disable the Nagle
                // algorithm, disable delayed ACKs, and enable TCP keep-alive.
                SocketConfigurator.ConfigureSocket(client.Client, enableKeepAlive: true);

                // Additionally, we use a smaller send buffer (32 KiB instead of 64 KiB) to
                // reduce buffer bloat between the proxy connections. This ensures e.g. window
                // updates will be forwarded with a shorter latency if the network is busy.
                // As for the receive buffer, it looks like even when we set it to a small value,
                // Windows will still buffer a lot more data than the receive
                // buffer size (in my tests it was about 2.5 MiB); I'm not yet sure what causes
                // this.
                client.Client.SendBufferSize = Constants.SocketSendBufferSize;

                var remoteEndpoint = client.Client.RemoteEndPoint!;
                this.logger?.Invoke($"Accepted connection from '{remoteEndpoint}'.");

                var endpoint = new TcpClientFramingEndpoint(
                    client,
                    useSendQueue: true,
                    usePingTimer: true,
                    streamModifier: streamModifier);

                var handler = new GatewayProxyConnectionHandler(this, endpoint, remoteEndpoint);

                lock (activeHandlers)
                {
                    // Start a task to handle the endpoint.
                    var runTask = ExceptionUtils.StartTask(async () =>
                    {
                        try
                        {
                            await endpoint.RunEndpointAsync(handler.RunAsync, cancellationToken);
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

                    activeHandlers.Add(handler, runTask);
                }
            }
        }
        finally
        {
            // Wait for the connections to finish.
            // Need to add the tasks in a separate list, because the handlers also
            // remove themselves from that dictionary.
            var tasksToWaitFor = new List<Task>();

            lock (activeHandlers)
            {
                tasksToWaitFor.AddRange(activeHandlers.Values);
            }

            // After releasing the lock, wait for the tasks.
            // Note: The tasks remove themselves from the dictionary, so a task might still
            // be executing  although it is not in the dictionary any more.
            // However this is OK because the task doesn't do anything after that point.
            foreach (var t in tasksToWaitFor)
                await t;
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
                            ServerCertificate = certificate,
                            EnabledSslProtocols = Constants.SslProtocols
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

