using System;
using System.Net.Sockets;

namespace SimpleSocketClient;

internal static class SocketConfigurator
{
    private static readonly byte[] IntOneAsBytes = BitConverter.GetBytes(1);

    /// <summary>
    /// Disables the Nagle algorithm and delayed ACKs for TCP sockets
    /// in order to improve response time.
    /// </summary>
    /// <remarks>
    /// You should call this method after the socket is connected, not before. Otherwise,
    /// methods such as <see cref="TcpClient.ConnectAsync(string, int)"/> may throw a
    /// <see cref="PlatformNotSupportedException"/> on non-Windows OSes. See:
    /// https://github.com/dotnet/runtime/issues/24917
    /// </remarks>
    /// <param name="socket"></param>
    public static void ConfigureSocket(Socket socket)
    {
        // Disable the Nagle algorithm (so that we don't delay new packets to be sent
        // when the remote party didn't ACK our previous packet(s) yet).
        socket.NoDelay = true;

        // Disable delayed ACK (so that if the remote party uses the Nagle algorithm,
        // we can reduce the time it has to wait until it can send us new packets).
        DisableTcpDelayedAck(socket);
    }

    private static void DisableTcpDelayedAck(Socket socket)
    {
        // See: https://github.com/dotnet/runtime/issues/798 for plans to integrate this
        // into a future .NET version.
        // Note: On Debian-based Linux OSes, it seems TCP_QUICKACK is already enabled
        // by default (according to the man page of socket_quickack(3).
        if (socket.ProtocolType == ProtocolType.Tcp && OperatingSystem.IsWindows())
        {
            try
            {
                const int SIO_TCP_SET_ACK_FREQUENCY = unchecked((int)0x98000017);
                socket.IOControl(SIO_TCP_SET_ACK_FREQUENCY, IntOneAsBytes, Array.Empty<byte>());
            }
            catch (SocketException)
            {
                // Ignore; we don't want to fail.
            }
        }
    }
}
