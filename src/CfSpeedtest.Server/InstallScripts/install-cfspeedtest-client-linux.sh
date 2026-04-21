#!/usr/bin/env bash
set -euo pipefail

SERVER_URL=""
CLIENT_ID=""
ISP="Telecom"
CLIENT_NAME=""
REPOSITORY=""
RELEASE_TAG=""
GH_PROXY_PREFIX=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server) SERVER_URL="${2:-}"; shift 2 ;;
    --client-id) CLIENT_ID="${2:-}"; shift 2 ;;
    --isp) ISP="${2:-}"; shift 2 ;;
    --name) CLIENT_NAME="${2:-}"; shift 2 ;;
    --repository) REPOSITORY="${2:-}"; shift 2 ;;
    --release-tag) RELEASE_TAG="${2:-}"; shift 2 ;;
    --gh-proxy-prefix) GH_PROXY_PREFIX="${2:-}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

log() { echo "[CfSpeedtest] $1"; }
fail() { echo "[CfSpeedtest] $1" >&2; exit 1; }

if [[ -z "$SERVER_URL" || -z "$CLIENT_ID" || -z "$REPOSITORY" || -z "$RELEASE_TAG" ]]; then
  fail "Usage: --server <url> --client-id <id> [--isp <Telecom|Unicom|Mobile>] [--name <name>] --repository <owner/repo> --release-tag <tag> [--gh-proxy-prefix <prefix>]"
fi

if [ "${EUID}" -ne 0 ]; then
  fail "请使用 root 运行此脚本"
fi

if [[ -z "$CLIENT_NAME" ]]; then
  CLIENT_NAME="${ISP}-${CLIENT_ID:0:8}"
fi

detect_rid() {
  local arch libc
  arch="$(uname -m)"
  libc="glibc"
  if command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; then
    libc="musl"
  fi

  case "$arch" in
    x86_64|amd64)
      if [ "$libc" = "musl" ]; then echo "linux-musl-x64"; else echo "linux-x64"; fi
      ;;
    aarch64|arm64)
      if [ "$libc" = "musl" ]; then echo "linux-musl-arm64"; else echo "linux-arm64"; fi
      ;;
    armv7l|armv7|armhf|arm)
      echo "linux-arm"
      ;;
    *)
      fail "不支持的 Linux 架构: $arch"
      ;;
  esac
}

build_download_url() {
  local asset="$1"
  local raw_url="https://github.com/${REPOSITORY}/releases/download/${RELEASE_TAG}/${asset}"
  if [[ -n "$GH_PROXY_PREFIX" ]]; then
    echo "${GH_PROXY_PREFIX%/}/${raw_url}"
  else
    echo "$raw_url"
  fi
}

ensure_dependencies() {
  local missing=0
  command -v curl >/dev/null 2>&1 || missing=1
  command -v unzip >/dev/null 2>&1 || missing=1
  command -v systemctl >/dev/null 2>&1 || missing=1
  if [ "$missing" -eq 0 ]; then
    return
  fi

  if command -v apt-get >/dev/null 2>&1; then
    apt-get update
    apt-get install -y curl unzip systemd
  elif command -v yum >/dev/null 2>&1; then
    yum install -y curl unzip systemd
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y curl unzip systemd
  elif command -v apk >/dev/null 2>&1; then
    apk add --no-cache curl unzip
  else
    fail "缺少依赖，请先安装 curl、unzip 和 systemd"
  fi
}

RID="$(detect_rid)"
PACKAGE_ASSET="cfspeedtest-client-${RID}.zip"
DOWNLOAD_URL="$(build_download_url "$PACKAGE_ASSET")"

INSTALL_DIR="/opt/cfspeedtest-client"
SERVICE_NAME="cfspeedtest-client"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
ZIP_PATH="/tmp/${PACKAGE_ASSET}"
STAGE_DIR="$(mktemp -d /tmp/cfspeedtest-client.XXXXXX)"

log "平台: ${RID}"
log "下载地址: ${DOWNLOAD_URL}"

ensure_dependencies
mkdir -p "$INSTALL_DIR"

log "下载客户端..."
curl -fL --retry 3 --connect-timeout 15 -o "$ZIP_PATH" "$DOWNLOAD_URL"

log "解压客户端..."
unzip -oq "$ZIP_PATH" -d "$STAGE_DIR"
cp -fR "$STAGE_DIR"/. "$INSTALL_DIR"/
chmod +x "$INSTALL_DIR/CfSpeedtest.Client"

log "写入 systemd 服务..."
cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=CfSpeedtest Client
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/CfSpeedtest.Client --server $SERVER_URL --client-id $CLIENT_ID --isp $ISP --name $CLIENT_NAME --service
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

rm -rf "$STAGE_DIR"
rm -f "$ZIP_PATH"

log "安装完成"
log "查看状态: systemctl status ${SERVICE_NAME}"
log "查看日志: journalctl -u ${SERVICE_NAME} -f"
