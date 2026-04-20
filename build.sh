#!/bin/bash
set -e

echo "=== Building Cloudflare SpeedTest ==="
echo

echo "[1/3] Building Server..."
dotnet publish src/CfSpeedtest.Server/CfSpeedtest.Server.csproj -c Release -o publish/server

echo
echo "[2/3] Building Client (NativeAOT - Linux x64)..."
dotnet publish src/CfSpeedtest.Client/CfSpeedtest.Client.csproj -c Release -r linux-x64 --self-contained -o publish/client-linux-x64

echo
echo "[3/3] Building Client (NativeAOT - Linux ARM64)..."
dotnet publish src/CfSpeedtest.Client/CfSpeedtest.Client.csproj -c Release -r linux-arm64 --self-contained -o publish/client-linux-arm64

echo
echo "=== Build Complete ==="
echo "Server:              publish/server/"
echo "Client (Linux x64):  publish/client-linux-x64/CfSpeedtest.Client"
echo "Client (Linux ARM):  publish/client-linux-arm64/CfSpeedtest.Client"
echo
echo "=== Usage ==="
echo "Server:  dotnet publish/server/CfSpeedtest.Server.dll"
echo "         WebUI: http://localhost:5000"
echo
echo "Client:  ./publish/client-linux-x64/CfSpeedtest.Client --server http://SERVER:5000 --isp Telecom --name MyNode"
echo "         Options: --isp Telecom/Unicom/Mobile  --interval 300  --once  --name NodeName"
