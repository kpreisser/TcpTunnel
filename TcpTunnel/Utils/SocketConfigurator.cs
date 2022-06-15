using System;
using System.Net.Sockets;

namespace SimpleSocketClient;

internal static class SocketConfigurator
{
    private static readonly byte[] IntOneAsBytes = BitConverter.GetBytes(1);

    private static readonly Version Win10Version1703 = new(10, 0, 15063);

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
    public static void ConfigureSocket(Socket socket, bool enableKeepAlive = false)
    {
        // Disable the Nagle algorithm (so that we don't delay new packets to be sent
        // when the remote party didn't ACK our previous packet(s) yet).
        socket.NoDelay = true;

        // Disable delayed ACK (so that if the remote party uses the Nagle algorithm,
        // we can reduce the time it has to wait until it can send us new packets).
        DisableTcpDelayedAck(socket);

        // Note: Calling Socket.SetSocketOption (on .NET Core 3.0 and higher) for
        // all three keep-alive settings (Time, Interval, RetryCount) on Windows
        // seems to be only supported starting with Windows 10 Build 15063; on
        // earlier Windows versions it would cause the connection to fail.
        if (enableKeepAlive &&
            (!OperatingSystem.IsWindows() ||
                OperatingSystem.IsWindowsVersionAtLeast(Win10Version1703.Major, Win10Version1703.Minor, Win10Version1703.Build)))
        {
            // Set the specified keep alive values.
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.KeepAlive,
                    true);

                // Use values that will detect a broken connection after 30 seconds.
                socket.SetSocketOption(
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveTime,
                    15);

                socket.SetSocketOption(
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveInterval,
                    3);

                socket.SetSocketOption(
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveRetryCount,
                    5);
            }
            catch (SocketException)
            {
                // Ignore; we don't want to fail.
            }
        }
    }

    private static void DisableTcpDelayedAck(Socket socket)
    {
        // See: https://github.com/dotnet/runtime/issues/798 for plans to integrate this
        // into a future .NET version.
        // Note: On Debian-based Linux OSes, it seems TCP_QUICKACK is already enabled
        // by default (according to the man page of socket_quickack(3).
        if (socket.ProtocolType is ProtocolType.Tcp && OperatingSystem.IsWindows())
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
