FROM alpine:3.22

ARG TARGETARCH
WORKDIR /app
RUN apk add --no-cache ca-certificates icu-libs tzdata
COPY container/client-${TARGETARCH}/ /app/
COPY docker/client-entrypoint.sh /usr/local/bin/cftest-agent-entrypoint
RUN chmod +x /app/cftest-agent /usr/local/bin/cftest-agent-entrypoint

ENV CF_SERVER_URL=http://server:5000 CF_ISP=Telecom CF_CLIENT_NAME=docker-client CF_INTERVAL=60
ENTRYPOINT ["/usr/local/bin/cftest-agent-entrypoint"]
