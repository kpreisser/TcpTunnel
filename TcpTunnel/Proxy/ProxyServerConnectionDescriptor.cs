using System;
using System.Net;

namespace TcpTunnel.Proxy;

public class ProxyServerConnectionDescriptor
{
    public ProxyServerConnectionDescriptor(
        IPAddress? listenIP,
        int listenPort,
        string remoteHost,
        int remotePort)
    {
        this.ListenIP = listenIP;
        this.ListenPort = listenPort;
        this.RemoteHost = remoteHost ?? throw new ArgumentNullException(nameof(remoteHost));
        this.RemotePort = remotePort;
    }

    public IPAddress? ListenIP
    {
        get;
    } 

    public int ListenPort
    {
        get;
    }

    public string RemoteHost
    {
        get;
    }

    public int RemotePort
    {
        get;
    }
}
