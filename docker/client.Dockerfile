FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CfSpeedtest.Shared/CfSpeedtest.Shared.csproj src/CfSpeedtest.Shared/
COPY src/CfSpeedtest.Client/CfSpeedtest.Client.csproj src/CfSpeedtest.Client/
RUN dotnet restore src/CfSpeedtest.Client/CfSpeedtest.Client.csproj
COPY src/CfSpeedtest.Shared/ src/CfSpeedtest.Shared/
COPY src/CfSpeedtest.Client/ src/CfSpeedtest.Client/
RUN dotnet publish src/CfSpeedtest.Client/CfSpeedtest.Client.csproj -c Release -o /app/publish -p:PublishAot=false -p:PublishSingleFile=false --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./
COPY docker/client-entrypoint.sh /usr/local/bin/cfspeedtest-client
RUN chmod +x /usr/local/bin/cfspeedtest-client
ENV CF_SERVER_URL=http://server:5000 CF_ISP=Telecom CF_CLIENT_NAME=docker-client CF_INTERVAL=60
ENTRYPOINT ["/usr/local/bin/cfspeedtest-client"]
