# TcpTunnel

TcpTunnel is a program implemented in C# (.NET 7.0) that allows to tunnel TCP connections through a server (gateway)
to a remote machine, for example to access services that are running behind a firewall or NAT
(like a reverse-connect mechanism).

A working configuration consists of three nodes:
- **Gateway**: Runs on a (server) machine that is accessible for both proxy endpoints (e.g. on a public server).
  It listens for incoming TCP connections from the proxy endpoints (*proxy-client* and *proxy-server*) and forwards
  data from one proxy endpoint to the corresponding partner proxy endpoint.
- **Proxy-Server**: Connects to the *Gateway* and listens for incoming TCP connections on previously configured
  ports. When a connection arrives, it forwards it to the *Gateway*, which in turn forwards the connection to
  the *Proxy-Client*.
- **Proxy-Client**: Connects to the *Gateway* and waits for forwarded connections received through the
  *Gateway* from the *Proxy-Server*. When receiving such a forwarded connection, it opens a TCP connection
  to the specified target endpoint and forwards the data to it.

For example, imagine you have some TCP service (like a VNC server) running on a machine within a LAN that
has internet access (maybe only through NAT so it's not possible to use port forwarding or a VPN), and you
want to securely connect to this service from a machine on another network.
Additionally, you have a server (e.g. VPS) with a public domain and you have a SSL certificate for it.

In this case, you could use the TcpTunnel with the following configuration:
- Run the **Gateway** on the VPS and configure it to listen at a specific TCP port using SSL/TLS, and to
  allow a session with an ID and password.
- Run the **Proxy-Client** on the machine that has access to the TCP service (VNC server) and configure it to
  connect to the host and port of the Gateway.
- Run the **Proxy-Server** on your machine where you want to access the TCP service (VNC server), and configure
  it to connect to the host and port of the Gateway, and to listen on a specific TCP port (like 5920) that
  should get forwarded to the Proxy-Client to a specific target host and TCP port (like localhost:5900).

The following image illustrates this scenario:
![](tcptunnel-illustration.png)

## Configuration

The TcpTunnel is configured via an XML file with the name `settings.xml` in the application's directory.
When building the application, sample setting files will get copied to the output directory which you can
use as a template.

## Features

- Uses async I/O for high scalability.
- Supports SSL/TLS and password authentication for the connections from the Proxies to the Gateway.
- Multiplexes multiple (tunneled) TCP connections over a single connection, similar to the stream concept in HTTP/2.
- Uses flow control for the tunneled TCP connections (using a initial window size of 384 KiB), similar
  to the flow control mechanism used in HTTP/2.
- Automatically recovers after one of the nodes (*Gateway*, *Proxy-Server*, *Proxy-Client*) was temporarily unavailable.
- On Windows, it can be installed as service.

## Building:
- Install the [.NET 7.0 SDK](https://dotnet.microsoft.com/download) or higher.
- On Windows, you can use one of the `PUBLISH-xyz.cmd` files to publish the app, either as self-contained app
  (with native AOT compilation), or as framework-dependent app (so it needs the .NET Runtime to be installed).
- Otherwise, you can publish for the current platform with the following command (as framework-dependent app): 
  ```
  dotnet publish "TcpTunnel/TcpTunnel.csproj" -f net7.0 -c Release -p:PublishSingleFile=true --no-self-contained
  ```

## Possible Development TODOs

- Use a different password storage mechanism so that they don't have to be specified in cleartext in the
  XML settings file.
- Add more documentation.
- Support installing/running as service on Linux e.g. via `systemd`.
