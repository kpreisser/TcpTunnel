using System;
using System.Collections.Generic;

namespace TcpTunnel.Gateway;

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

    // Contains the partner proxies, keyed by the Proxy ID.
    // Proxy 0 is always the proxy-client; the other ones are proxy-servers.
    public Dictionary<long, GatewayConnectionHandler> Proxies
    {
        get;
    } = new();

    public long NextProxyId
    {
        get;
        set;
    } = 1;
}
