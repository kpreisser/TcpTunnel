using System;

namespace TcpTunnel.Server;

internal class Session
{
    public Session(ReadOnlyMemory<byte> passwordBytes)
    {
        this.PasswordBytes = passwordBytes;
    }

    public ReadOnlyMemory<byte> PasswordBytes
    {
        get;
    }

    // Contains the partner clients.
    public ServerConnectionHandler?[] Clients
    {
        get;
    } = new ServerConnectionHandler?[2];
}
