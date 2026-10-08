import { ispKey } from "./isp";
import type { BootstrapTokenCreateResponse } from "./types";

const image = "ghcr.io/greepar/cfspeedtest-client:latest";
const shellQuote = (value: string) => `'${value.replace(/'/g, `'"'"'`)}'`;

export function containerDeploy(res: BootstrapTokenCreateResponse) {
  const name = `cfspeedtest-client-${res.clientId}`;
  const environment = {
    CF_SERVER_URL: res.serverUrl,
    CF_ISP: ispKey(res.isp),
    CF_CLIENT_NAME: res.name,
    CF_CLIENT_ID: res.clientId,
    CF_INTERVAL: "60",
    CF_DISABLE_AUTO_UPDATE: res.disableAutoUpdate ? "1" : "0",
  };
  return {
    run: [
      "docker run -d",
      `  --name ${shellQuote(name)}`,
      "  --restart unless-stopped",
      ...Object.entries(environment).map(
        ([key, value]) => `  -e ${shellQuote(`${key}=${value}`)}`,
      ),
      `  ${image}`,
    ].join(" \\\n"),
    start: `curl -fL ${shellQuote(res.composeUrl)} -o compose.yml && docker compose -f compose.yml up -d`,
    update:
      "docker compose -f compose.yml pull && docker compose -f compose.yml up -d",
  };
}
