using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
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
    // The max receive message size mainly arises from the tunnel connection receive buffer size
    // (32 KiB) plus the additional metadata, which are just a few bytes. Therefore, 256 KiB
    // should be more than enough.
    // (But note that e.g. for the target host defined by the user that is sent to the
    // proxy-client, there is currently no limit enforced, so such messages might theoretically
    // exceed this size.)
    public const int MaxReceiveMessageSize = 256 * 1024;

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

    public static string FormatSslProtocol(SslProtocols protocol)
    {
        return protocol switch
        {
#pragma warning disable SYSLIB0039 // Type or member is obsolete
            SslProtocols.Tls => "TLS 1.0",
            SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore SYSLIB0039 // Type or member is obsolete
            SslProtocols.Tls12 => "TLS 1.2",
            SslProtocols.Tls13 => "TLS 1.3",
            var other => other.ToString()
        };
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
                catch (Exception ex) when (ex.CanCatch())
                {
                    // Stop() will dispose the underlying socket.
                    try
                    {
                        listener?.Stop();
                    }
                    catch (Exception ex2) when (ex2.CanCatch())
                    {
                        // Ignore.
                    }

                    // Stop the previously started listeners, then rethrow the exception.
                    this.Stop();
                    throw;
                }

                this.logger?.Invoke(
                    $"Gateway listener started on '{ip?.ToString() ?? "<any>"}:{port.ToString(CultureInfo.InvariantCulture)}' (Using SSL: {certificate is not null}).");

                var listenerTask = ExceptionUtils.StartTask(
                    () => this.RunListenerTaskAsync(listener, certificate, this.listenersCts.Token));

                this.activeListeners.Add((listener, listenerTask));
            }
        }
        catch (Exception ex) when (ex.CanCatch())
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

    private async Task RunListenerTaskAsync(
        TcpListener listener,
        X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        var activeHandlers = new Dictionary<GatewayProxyConnectionHandler, Task>();

        try
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync(cancellationToken);
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
                SocketConfigurator.ConfigureSocket(socket, enableKeepAlive: true);

                // Additionally, we use a smaller send buffer (32 KiB instead of 64 KiB) to
                // reduce buffer bloat between the proxy connections. This ensures e.g. window
                // updates will be forwarded with a shorter latency if the network is busy.
                // As for the receive buffer, it looks like even when we set it to a small value,
                // Windows will still buffer a lot more data than the receive
                // buffer size (in my tests it was about 2.5 MiB); I'm not yet sure what causes
                // this.
                socket.SendBufferSize = Constants.SocketSendBufferSize;

                var remoteEndpoint = socket.RemoteEndPoint!;

                this.logger?.Invoke(
                    $"Proxy '{remoteEndpoint}': Connection accepted. " +
                    $"Establishing SSL: {certificate is not null}...");

                var proxyConnection = new TcpFramingConnection(
                    socket,
                    useSendQueue: true,
                    usePingTimer: true,
                    streamModifier: async (networkStream, cancellationToken) =>
                    {
                        if (certificate is not null)
                        {
                            var sslNegotiatedProtocol = default(SslProtocols);
                            var sslNegotiatedCipherSuite = default(TlsCipherSuite);

                            var sslStream = new SslStream(networkStream);
                            try
                            {
                                await sslStream.AuthenticateAsServerAsync(
                                    new SslServerAuthenticationOptions()
                                    {
                                        ServerCertificate = certificate
                                    },
                                    cancellationToken);

                                sslNegotiatedProtocol = sslStream.SslProtocol;
                                sslNegotiatedCipherSuite = sslStream.NegotiatedCipherSuite;
                            }
                            catch (Exception ex) when (ex.CanCatch())
                            {
                                await sslStream.DisposeAsync();
                                throw;
                            }

                            this.logger?.Invoke(
                                $"Proxy '{remoteEndpoint}': Established SSL " +
                                $"(Protocol: {FormatSslProtocol(sslNegotiatedProtocol)}, " +
                                $"Cipher Suite: {sslNegotiatedCipherSuite}).");

                            return sslStream;
                        }

                        return null;
                    });

                var handler = new GatewayProxyConnectionHandler(this, proxyConnection, remoteEndpoint);

                lock (activeHandlers)
                {
                    // Start a task to handle the connection.
                    var runTask = ExceptionUtils.StartTask(async () =>
                    {
                        var caughtException = default(Exception);

                        try
                        {
                            await proxyConnection.RunConnectionAsync(handler.RunAsync, cancellationToken);
                        }
                        catch (Exception ex) when (ex.CanCatch())
                        {
                            caughtException = ex;
                        }
                        finally
                        {
                            try
                            {
                                socket.Dispose();
                            }
                            catch (Exception ex) when (ex.CanCatch())
                            {
                                // Ignore.
                            }

                            this.logger?.Invoke(
                                $"Proxy '{remoteEndpoint}': Connection closed" +
                                (caughtException is null ? string.Empty : $" ({caughtException.Message})") +
                                ".");

                            // Finally, remove the handler. This must be the last action
                            // done in the task.
                            lock (activeHandlers)
                            {
                                activeHandlers.Remove(handler);
                            }
                        }
                    });

                    try
                    {
                        activeHandlers.Add(handler, runTask);
                    }
                    catch (Exception ex) when (ex.CanCatch())
                    {
                        // There are too many entries in the dictionary
                        // (should never happen in practice).
                        Environment.FailFast(ex.Message, ex);
                        throw; // Satisfy CFA
                    }
                }
            }
        }
        catch (Exception ex) when (ex.CanCatch() && false)
        {
            // We need a separate exception filter to prevent the finally handler
            // from being called in case of an OOME.
            throw;
        }
        finally
        {
            // Wait for the connections to finish.
            // Need to add the tasks in a separate list, because the handlers also
            // remove themselves from that dictionary.
            Task[] tasksToWaitFor;

            lock (activeHandlers)
            {
                tasksToWaitFor = activeHandlers.Values.ToArray();
            }

            // After releasing the lock, wait for the tasks.
            // Note: The tasks remove themselves from the dictionary, so a task might still
            // be executing although it is not in the dictionary any more.
            // However this is OK because the task doesn't do anything after that point.
            foreach (var t in tasksToWaitFor)
                await t;
        }
    }

    public void Dispose()
    {
        this.Stop();
    }
}

