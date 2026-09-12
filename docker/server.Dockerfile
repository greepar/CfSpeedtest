FROM alpine:3.22

ARG TARGETARCH
WORKDIR /app
RUN apk add --no-cache ca-certificates icu-libs tzdata
COPY container/server-${TARGETARCH}/ /app/

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
VOLUME ["/app/data", "/app/client-updates"]
ENTRYPOINT ["/app/CfSpeedtest.Server"]
