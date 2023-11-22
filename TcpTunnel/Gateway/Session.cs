using System;
using System.Collections.Generic;

namespace TcpTunnel.Gateway;

internal class Session
{
    public Session(
        ReadOnlyMemory<byte> proxyClientPasswordBytes,
        ReadOnlyMemory<byte> proxyServerPasswordBytes)
    {
        this.ProxyClientPasswordBytes = proxyClientPasswordBytes;
        this.ProxyServerPasswordBytes = proxyServerPasswordBytes;
    }

    public ReadOnlyMemory<byte> ProxyClientPasswordBytes
    {
        get;
    }

    public ReadOnlyMemory<byte> ProxyServerPasswordBytes
    {
        get;
    }

    // Contains the partner proxies, keyed by the Proxy ID.
    // Proxy 0 is always the proxy-client; the other ones are proxy-servers.
    public Dictionary<ulong, GatewayProxyConnectionHandler> Proxies
    {
        get;
    } = [];

    public ulong NextProxyId
    {
        get;
        set;
    } = 1;
}
