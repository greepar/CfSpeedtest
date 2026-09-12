#!/bin/sh
set -eu

set -- \
  --server "${CF_SERVER_URL:-http://server:5000}" \
  --isp "${CF_ISP:-Telecom}" \
  --name "${CF_CLIENT_NAME:-docker-client}" \
  --interval "${CF_INTERVAL:-60}"

if [ -n "${CF_CLIENT_ID:-}" ]; then
  set -- "$@" --client-id "$CF_CLIENT_ID"
fi
if [ -n "${CF_DISABLE_AUTO_UPDATE:-}" ]; then
  set -- "$@" --disable-auto-update
fi

exec /app/CfSpeedtest.Client "$@"
