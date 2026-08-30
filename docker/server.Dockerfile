FROM node:22-alpine AS web-build
WORKDIR /src
COPY src/CfSpeedtest.Web/package*.json ./src/CfSpeedtest.Web/
RUN npm ci --prefix src/CfSpeedtest.Web
COPY src/CfSpeedtest.Web/ ./src/CfSpeedtest.Web/
RUN npm run build --prefix src/CfSpeedtest.Web

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /src
COPY src/CfSpeedtest.Shared/CfSpeedtest.Shared.csproj src/CfSpeedtest.Shared/
COPY src/CfSpeedtest.Server/CfSpeedtest.Server.csproj src/CfSpeedtest.Server/
RUN case "$TARGETARCH" in \
      amd64) RID=linux-musl-x64 ;; \
      arm64) RID=linux-musl-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && dotnet restore src/CfSpeedtest.Server/CfSpeedtest.Server.csproj -r "$RID"
COPY src/CfSpeedtest.Shared/ src/CfSpeedtest.Shared/
COPY src/CfSpeedtest.Server/ src/CfSpeedtest.Server/
COPY --from=web-build /src/src/CfSpeedtest.Server/wwwroot/ src/CfSpeedtest.Server/wwwroot/
RUN case "$TARGETARCH" in \
      amd64) RID=linux-musl-x64 ;; \
      arm64) RID=linux-musl-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/CfSpeedtest.Server/CfSpeedtest.Server.csproj -c Release -r "$RID" --self-contained false -o /app/publish -p:PublishAot=false -p:PublishSingleFile=false -p:SkipBuildWebUi=true --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
COPY --from=build /app/publish/ ./
VOLUME ["/app/data", "/app/client-updates"]
ENTRYPOINT ["dotnet", "/app/CfSpeedtest.Server.dll"]
