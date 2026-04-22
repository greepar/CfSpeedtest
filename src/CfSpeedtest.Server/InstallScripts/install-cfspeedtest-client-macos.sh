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
  fail "请使用 sudo 执行此脚本，例如：curl -fsSL <script-url> | sudo bash -s -- ..."
fi

if [[ -z "$CLIENT_NAME" ]]; then
  CLIENT_NAME="${ISP}-${CLIENT_ID:0:8}"
fi

detect_rid() {
  case "$(uname -m)" in
    arm64) echo "osx-arm64" ;;
    x86_64|amd64) echo "osx-x64" ;;
    *) fail "不支持的 macOS 架构" ;;
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

RID="$(detect_rid)"
PACKAGE_ASSET="cfspeedtest-client-${RID}.zip"
DOWNLOAD_URL="$(build_download_url "$PACKAGE_ASSET")"

INSTALL_DIR="/usr/local/cfspeedtest-client"
PLIST_NAME="uk.greepar.cfspeedtest.client"
PLIST_PATH="/Library/LaunchDaemons/${PLIST_NAME}.plist"
ZIP_PATH="/tmp/${PACKAGE_ASSET}"
STAGE_DIR="$(mktemp -d /tmp/cfspeedtest-client.XXXXXX)"

log "平台: ${RID}"
log "下载地址: ${DOWNLOAD_URL}"

mkdir -p "$INSTALL_DIR"

if launchctl print "system/${PLIST_NAME}" >/dev/null 2>&1; then
  log "检测到已存在的 launchd 服务，准备覆盖更新..."
  launchctl bootout "system/${PLIST_NAME}" >/dev/null 2>&1 || true
fi

curl -fL --retry 3 --connect-timeout 15 -o "$ZIP_PATH" "$DOWNLOAD_URL"
unzip -oq "$ZIP_PATH" -d "$STAGE_DIR"
cp -fR "$STAGE_DIR"/. "$INSTALL_DIR"/
chmod +x "$INSTALL_DIR/CfSpeedtest.Client"

cat > "$PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${PLIST_NAME}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${INSTALL_DIR}/CfSpeedtest.Client</string>
    <string>--server</string>
    <string>${SERVER_URL}</string>
    <string>--client-id</string>
    <string>${CLIENT_ID}</string>
    <string>--isp</string>
    <string>${ISP}</string>
    <string>--name</string>
    <string>${CLIENT_NAME}</string>
    <string>--service</string>
  </array>
  <key>WorkingDirectory</key>
  <string>${INSTALL_DIR}</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardOutPath</key>
  <string>/var/log/cfspeedtest-client.log</string>
  <key>StandardErrorPath</key>
  <string>/var/log/cfspeedtest-client.err.log</string>
</dict>
</plist>
EOF

chmod 644 "$PLIST_PATH"
launchctl bootout system "$PLIST_PATH" >/dev/null 2>&1 || true
launchctl bootstrap system "$PLIST_PATH"
launchctl enable "system/${PLIST_NAME}"
launchctl kickstart -k "system/${PLIST_NAME}"

rm -rf "$STAGE_DIR"
rm -f "$ZIP_PATH"

log "安装完成"
log "查看状态: sudo launchctl print system/${PLIST_NAME}"
log "查看日志: tail -f /var/log/cfspeedtest-client.log"
