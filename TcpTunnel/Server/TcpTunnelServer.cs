using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

namespace TcpTunnel.Server;

public class TcpTunnelServer
{
    public const int MaxReceivePacketSize = 2 * 1024 * 1024;
    public const int MaxSendBufferSize = 5 * 1024 * 1024;

    private readonly int port;
    private readonly X509Certificate2? certificate;

    private readonly TcpListener listener;
    private readonly Dictionary<ServerConnectionHandler, (Task task, bool canCancelEndpoint)> activeHandlers = new();

    private readonly Action<string>? logger;

    private Task? listenerTask;
    private bool stopped;

    public TcpTunnelServer(
        int port,
        X509Certificate2? certificate,
        IDictionary<int, string> sessions,
        Action<string>? logger = null)
    {
        this.port = port;
        this.certificate = certificate;
        this.logger = logger;

        this.SyncRoot = new();

        var sessionDictionary = new Dictionary<int, Session>();
        this.Sessions = sessionDictionary;

        foreach (var pair in sessions)
            sessionDictionary.Add(pair.Key, new Session(Encoding.UTF8.GetBytes(pair.Value)));

        this.listener = TcpListener.Create(port);
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
        this.logger?.Invoke(
            $"Listener started on port '{this.port.ToString(CultureInfo.InvariantCulture)}'.");

        this.listener.Start();
        this.listenerTask = ExceptionUtils.StartTask(this.RunListenerTask);
    }

    public void Stop()
    {
        Volatile.Write(ref this.stopped, true);
        this.listener.Stop();

        // Wait for the listener task.
        this.listenerTask!.Wait();
        this.listenerTask.Dispose();
    }

    private async Task RunListenerTask()
    {
        bool isStopped = false;

        try
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await this.listener.AcceptTcpClientAsync();
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

                var remoteEndpoint = client.Client.RemoteEndPoint!;
                this.logger?.Invoke($"Accepted connection from '{remoteEndpoint}'.");

                var handler = default(ServerConnectionHandler);
                var endpoint = new TcpClientFramingEndpoint(
                    client,
                    useSendQueue: true,
                    usePingTimer: true,
                    connectHandler: cancellationToken =>
                    {
                        // Beginning from this stage, the endpoint can be
                        // canceled.
                        lock (this.activeHandlers)
                        {
                            if (isStopped)
                                throw new OperationCanceledException();

                            CollectionsMarshal.GetValueRefOrNullRef(this.activeHandlers, handler!)
                                .canCancelEndpoint = true;
                        }

                        return default;
                    },
                    closeHandler: () =>
                    {
                        // Once this handler returns, the endpoint may no longer
                        // be canceled.
                        lock (this.activeHandlers)
                        {
                            CollectionsMarshal.GetValueRefOrNullRef(this.activeHandlers, handler!)
                                .canCancelEndpoint = false;
                        }
                    },
                    streamModifier: this.ModifyStreamAsync);

                handler = new ServerConnectionHandler(this, endpoint, remoteEndpoint);

                lock (this.activeHandlers)
                {
                    // Start a task to handle the endpoint.
                    var runTask = ExceptionUtils.StartTask(async () =>
                    {
                        try
                        {
                            await endpoint.RunEndpointAsync(handler.RunAsync);
                        }
                        catch (Exception ex) when (ex.CanCatch())
                        {
                            // Ignore.
                        }
                        finally
                        {
                            client.Dispose();

                            lock (this.activeHandlers)
                            {
                                // Remove the handler.
                                this.activeHandlers.Remove(handler);
                            }

                            this.logger?.Invoke($"Closed connection from '{remoteEndpoint}'.");
                        }
                    });

                    this.activeHandlers.Add(handler, (runTask, false));
                }
            }
        }
        finally
        {
            // Stop all active clients.
            // Need to add the tasks in a separate list, because we cannot wait for them
            // while we hold the lock for activeHandlers, otherwise a deadlock might occur
            // because the handlers also remove themselves from that dictionary.
            var tasksToWaitFor = new List<Task>();
            lock (this.activeHandlers)
            {
                isStopped = true;

                foreach (var cl in this.activeHandlers)
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
    }

    private async ValueTask<Stream?> ModifyStreamAsync(
        NetworkStream networkStream,
        CancellationToken cancellationToken)
    {
        if (this.certificate is null)
        {
            return null;
        }
        else
        {
            var sslStream = new SslStream(networkStream);
            try
            {
                await sslStream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions()
                    {
                        // AllowRenegotiation is still true by default up to .NET 6.0.
                        AllowRenegotiation = false,
                        ServerCertificate = this.certificate,
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
    }

    public void Dispose()
    {
        this.Stop();
    }
}

