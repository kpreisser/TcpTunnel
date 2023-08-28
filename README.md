# TcpTunnel

TcpTunnel is a program implemented in C# (.NET 7.0) that allows to tunnel TCP connections through a server (gateway)
to a remote machine, for example to access services that are running behind a firewall or NAT
(like a reverse-connect mechanism).

A working configuration consists of three instances:
- **Gateway**: Runs on a (server) machine that is accessible for both proxy endpoints (e.g. on a public server).
  It listens for incoming TCP connections from the proxy endpoints (*proxy-client* and *proxy-server*) and forwards
  data from one proxy endpoint to the corresponding partner proxy endpoint.
- **Proxy-Server**: Connects to the *Gateway* and listens for incoming TCP connections on previously configured
  ports. When a connection arrives, it forwards it to the *Gateway*, which in turn forwards the connection to
  the *Proxy-Client*.
- **Proxy-Client**: Connects to the *Gateway* and waits for forwarded connections received through the
  *Gateway* from the *Proxy-Server*. When receiving such a forwarded connection, it opens a TCP connection
  to the specified target endpoint and forwards the data to it.

For example, imagine you have some TCP services (like a VNC and a SSH server) running on a machine within a LAN
that has internet access (maybe only through NAT so it's not possible to use port forwarding or a VPN), and you
want to securely connect to this service from a machine on another network.<br>
Additionally, you have a server (e.g. virtual private server, VPS) with a public domain and you have a
SSL/TLS certificate for it.

In this case, you could use the TcpTunnel with a configuration as shown in the following image:

![](tcptunnel-illustration.svg?raw=1)

That is:
- Run the **Gateway** on the VPS and configure it to listen at a specific TCP port using SSL/TLS, and to
  allow a session with an ID and password.
- Run the **Proxy-Client** on the machine that has access to the TCP services (VNC/SSH server), and configure
  it to connect to the host and port of the Gateway.
- Run the **Proxy-Server** on your machine where you want to access the TCP services (with a VNC/SSH client),
  and configure it to connect to the host and port of the Gateway, and to listen on a specific TCP port (like 5920)
  that should get forwarded to the Proxy-Client to a specific target host and TCP port (like 192.168.40.80:5900).

## Configuration

TcpTunnel is configured via an XML file with the name `settings.xml` in the application's directory.
When building the application, sample setting files will get copied to the output directory which you can
use as a template. You can also find them here for [Gateway](TcpTunnel/sample-settings-gateway.xml),
[Proxy-Server](TcpTunnel/sample-settings-proxy-server.xml) and
[Proxy-Client](TcpTunnel/sample-settings-proxy-client.xml).

You can define multiple instances (e.g. a Gateway and a Proxy-Server instance) in the settings file, which
will then be run by a single application process.

## Features

- Uses async I/O for high scalability.
- Supports SSL/TLS and password authentication for secure connections between the Gateway and the Proxies.
- Multiplexes multiple (tunneled) TCP connections over a single connection, similar to the stream concept in HTTP/2.
- Uses flow control for the tunneled TCP connections (using a initial window size of 384 KiB), similar
  to the flow control mechanism in HTTP/2.
- Multiple Proxy-Server instances can connect to a session, so it's possible to connect to the target
  endpoints from different machines at the same time.
- Automatically recovers after one of the nodes (*Gateway*, *Proxy-Server*, *Proxy-Client*) was temporarily unavailable.
- On Windows, it can be installed as service.

## Security Considerations

- The Gateway can define one or more password-protected *sessions* (which associate a
  Proxy-Client with one or more Proxy-Servers). In combination with enabling SSL/TLS in the Gateway and
  the Proxies, this ensures the connections between the Proxies and the Gateway are secure, and only the
  intended Proxies can connect to each other.
- You can specify different passwords for the Proxy-Client and the Proxy-Server(s) in the Gateway
  configuration. Additionally, it's possible to restrict the possible target endpoints (host and port)
  accepted by the Proxy-Client when receiving a forwarded connection from the Proxy-Server.<br>
  This allows the Proxy-Server and the Proxy-Client to operate at different trust levels, e.g. if you
  want to share the Proxy-Server to other people but want to allow them to only be able to connect to
  specific target endpoints.
- There are currently no special DoS protection mechanisms implemented (to handle the case when you don't
  control the partner proxy or the gateway), e.g. to limit the number of received forwarded connections,
  but there are basic limitations implemented, like limiting the max. received message size and ensuring
  that the partner proxy doesn't send more data for a connection than allowed by the flow control window.

## Building

- Install the [.NET 7.0 SDK](https://dotnet.microsoft.com/download) or higher.
- On Windows, you can use one of the `PUBLISH-xyz.cmd` files to publish the app, either as self-contained app
  (with native AOT compilation), or as framework-dependent app (so it needs the .NET Runtime to be installed).
- Otherwise, you can publish for the current platform with the following command (as self-contained app): 
  ```
  dotnet publish "TcpTunnel/TcpTunnel.csproj" -f net7.0 -c Release -p:PublishSingleFile=true --self-contained
  ```

## Possible Development TODOs

- Use a different password storage mechanism so that they don't have to be specified in cleartext in the
  XML settings file.
- Add more documentation.
- Support installing/running as service on Linux e.g. via `systemd`.
