FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /src
RUN apk add --no-cache clang lld build-base zlib-dev openssl-dev icu-data-full
COPY src/CfSpeedtest.Shared/CfSpeedtest.Shared.csproj src/CfSpeedtest.Shared/
COPY src/CfSpeedtest.Client/CfSpeedtest.Client.csproj src/CfSpeedtest.Client/
RUN case "$TARGETARCH" in \
      amd64) RID=linux-musl-x64 ;; \
      arm64) RID=linux-musl-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && dotnet restore src/CfSpeedtest.Client/CfSpeedtest.Client.csproj -r "$RID"
COPY src/CfSpeedtest.Shared/ src/CfSpeedtest.Shared/
COPY src/CfSpeedtest.Client/ src/CfSpeedtest.Client/
RUN case "$TARGETARCH" in \
      amd64) RID=linux-musl-x64 ;; \
      arm64) RID=linux-musl-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/CfSpeedtest.Client/CfSpeedtest.Client.csproj -c Release -r "$RID" --self-contained true -o /app/publish -p:PublishAot=true -p:PublishSingleFile=true --no-restore

FROM alpine:3.22 AS runtime
WORKDIR /app
RUN apk add --no-cache libstdc++ openssl
COPY --from=build /app/publish/ ./
COPY docker/client-entrypoint.sh /usr/local/bin/cfspeedtest-client
RUN chmod +x /usr/local/bin/cfspeedtest-client
ENV CF_SERVER_URL=http://server:5000 CF_ISP=Telecom CF_CLIENT_NAME=docker-client CF_INTERVAL=60
ENTRYPOINT ["/usr/local/bin/cfspeedtest-client"]
