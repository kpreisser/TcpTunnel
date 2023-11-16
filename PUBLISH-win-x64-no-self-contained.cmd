@echo off
SetLocal ENABLEDELAYEDEXPANSION
REM Change the working directory to the script's directory.
REM E.g. if the user right-clicks on the script and selects "Run as Administrator",
REM the working directory would be the windows\system32 dir.
cd /d "%~dp0"

echo.Building...
echo.

REM Note that we need to specify both "Configuration" and "Platform" parameters, because
REM otherwise MSBuild will fill missing parameters from environment variables (and some
REM systems may have set a "Platform" variable).
"dotnet.exe" publish "TcpTunnel\TcpTunnel.csproj" -f net8.0 -c Release -r win-x64 -p:Platform=AnyCPU -p:PublishSingleFile=true --no-self-contained
if not errorlevel 1 (
	echo.
	echo.Build successful^^!
)
pause
exit /b !ERRORLEVEL!