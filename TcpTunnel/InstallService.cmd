set "ServiceName=TcpTunnel"
sc create "%ServiceName%" binpath= "\"%~dp0TcpTunnel.exe\" -service" start= delayed-auto
sc failure "%ServiceName%" reset= 0 actions= "restart/60000/restart/60000/restart/60000"
@pause