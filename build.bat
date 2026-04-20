@echo off
echo === Building Cloudflare SpeedTest ===
echo.

echo [1/3] Building Server...
dotnet publish src\CfSpeedtest.Server\CfSpeedtest.Server.csproj -c Release -o publish\server
if errorlevel 1 goto :error

echo.
echo [2/3] Building Client (NativeAOT - Windows x64)...
dotnet publish src\CfSpeedtest.Client\CfSpeedtest.Client.csproj -c Release -r win-x64 --self-contained -o publish\client-win-x64
if errorlevel 1 goto :error

echo.
echo [3/3] Building Client (NativeAOT - Linux x64)...
dotnet publish src\CfSpeedtest.Client\CfSpeedtest.Client.csproj -c Release -r linux-x64 --self-contained -o publish\client-linux-x64
if errorlevel 1 goto :error

echo.
echo === Build Complete ===
echo Server:           publish\server\
echo Client (Win x64): publish\client-win-x64\CfSpeedtest.Client.exe
echo Client (Linux):   publish\client-linux-x64\CfSpeedtest.Client
echo.
echo === Usage ===
echo Server:  dotnet publish\server\CfSpeedtest.Server.dll
echo          or: cd publish\server ^&^& dotnet CfSpeedtest.Server.dll
echo          WebUI: http://localhost:5000
echo.
echo Client:  publish\client-win-x64\CfSpeedtest.Client.exe --server http://SERVER:5000 --isp Telecom --name MyNode
echo          Options: --isp Telecom/Unicom/Mobile  --interval 300  --once  --name NodeName
goto :end

:error
echo.
echo BUILD FAILED!
exit /b 1

:end
