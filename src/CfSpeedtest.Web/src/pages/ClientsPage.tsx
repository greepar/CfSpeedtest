import { useEffect, useState } from "react";
import {
  Copy,
  Edit3,
  FileText,
  Play,
  RefreshCw,
  Rocket,
  Trash2,
  UploadCloud,
} from "lucide-react";
import { api } from "@/lib/api";
import { containerDeploy } from "@/lib/containerDeploy";
import { formatDateTime, timeAgo } from "@/lib/format";
import { ISP_KEYS, ispBadgeTone, ispKey, ispLabel } from "@/lib/isp";
import type {
  BootstrapTokenCreateResponse,
  BootstrapTokenStatus,
  ClientInfo,
  ClientInstallScriptResponse,
  IspKey,
} from "@/lib/types";
import {
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  CodeBox,
  Empty,
  Field,
  Input,
  Modal,
  Progress,
  Select,
  Switch,
  Textarea,
  useToast,
} from "@/components/ui";

export function ClientsPage() {
  const toast = useToast();
  const [items, setItems] = useState<ClientInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [edit, setEdit] = useState<ClientInfo | null>(null);
  const [deploy, setDeploy] = useState<ClientInfo | null>(null);
  const [logClient, setLogClient] = useState<ClientInfo | null>(null);
  const [uninstall, setUninstall] = useState<ClientInfo | null>(null);

  async function load(showLoading = false) {
    if (showLoading || items.length === 0) setLoading(true);
    try {
      setItems((await api.get<ClientInfo[]>("/api/clients")) ?? []);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load(true).catch(() => setLoading(false));
    const t = window.setInterval(() => load(false).catch(() => {}), 10000);
    return () => window.clearInterval(t);
  }, []);

  async function doIt(fn: () => Promise<unknown>, msg: string) {
    await fn();
    toast(msg, "success");
    await load(false);
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title="客户端"
          desc="管理节点、触发测速/更新，以及生成一键部署命令"
          action={
            <>
              <Button variant="secondary" onClick={() => load(true)}>
                <RefreshCw className="h-4 w-4" />
                刷新
              </Button>
              <Button
                onClick={() =>
                  setDeploy({
                    clientId: "",
                    isp: 0,
                    name: "",
                    isOnline: false,
                    allowed: true,
                    registeredAt: "",
                    lastSeenAt: "",
                    currentTaskTestedIps: 0,
                    currentTaskTotalIps: 0,
                  })
                }
              >
                <Rocket className="h-4 w-4" />
                一键部署
              </Button>
            </>
          }
        />
        <CardBody>
          {loading && !items.length ? (
            <div className="py-8 text-center text-sm text-fg-muted">
              加载中...
            </div>
          ) : !items.length ? (
            <Empty title="暂无客户端" desc="点击一键部署创建第一个节点" />
          ) : (
            <div className="overflow-auto">
              <table className="w-full min-w-[980px] text-sm">
                <thead className="text-left text-xs text-fg-subtle">
                  <tr>
                    <th className="pb-3">节点</th>
                    <th>状态</th>
                    <th>运行</th>
                    <th>任务</th>
                    <th>版本/平台</th>
                    <th>最后心跳</th>
                    <th className="text-right">操作</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {items.map((c) => {
                    const pct = c.currentTaskTotalIps
                      ? (c.currentTaskTestedIps / c.currentTaskTotalIps) * 100
                      : 0;
                    const online = isClientOnline(c);
                    return (
                      <tr key={c.clientId}>
                        <td className="py-3">
                          <div className="font-medium text-fg">
                            {c.name || c.clientId.slice(0, 8)}
                          </div>
                          <div className="mt-1 flex items-center gap-2">
                            <Badge tone={ispBadgeTone(c.isp)}>
                              {ispLabel(c.isp)}
                            </Badge>
                            <span className="font-mono text-xs text-fg-subtle">
                              {c.clientId}
                            </span>
                          </div>
                        </td>
                        <td>
                          <div className="flex items-center gap-2">
                            <Badge tone={online ? "success" : "danger"}>
                              {online ? "在线" : "离线"}
                            </Badge>
                            <Switch
                              checked={c.allowed}
                              onChange={(v) =>
                                doIt(
                                  () =>
                                    api.post<string>(
                                      `/api/clients/${encodeURIComponent(c.clientId)}/allow`,
                                      undefined,
                                      { allowed: v },
                                    ),
                                  v
                                    ? "已启用测速调度"
                                    : "已暂停测速调度，客户端将保持连接",
                                )
                              }
                            />
                          </div>
                        </td>
                        <td className="max-w-48 truncate text-fg-muted">
                          {c.runtimeStatus || "-"}
                        </td>
                        <td className="w-44">
                          <Progress value={pct} />
                          <div className="mt-1 text-xs text-fg-subtle">
                            {c.currentTaskTestedIps}/{c.currentTaskTotalIps}
                          </div>
                        </td>
                        <td>
                          <div>{c.version || "-"}</div>
                          <div className="text-xs text-fg-subtle">
                            {c.platform === "docker"
                              ? "Docker"
                              : c.platform || "-"}
                          </div>
                        </td>
                        <td>
                          <div>{timeAgo(c.lastSeenAt)}</div>
                          <div className="text-xs text-fg-subtle">
                            {formatDateTime(c.lastSeenAt)}
                          </div>
                        </td>
                        <td>
                          <div className="flex justify-end gap-1">
                            <Button
                              variant="ghost"
                              size="icon"
                              title="日志"
                              onClick={() => setLogClient(c)}
                            >
                              <FileText className="h-4 w-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              title="编辑"
                              onClick={() => setEdit(c)}
                            >
                              <Edit3 className="h-4 w-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              title="测速"
                              onClick={() =>
                                doIt(
                                  () =>
                                    api.post<string>(
                                      `/api/clients/${encodeURIComponent(c.clientId)}/trigger-test`,
                                    ),
                                  "已触发测速",
                                )
                              }
                            >
                              <Play className="h-4 w-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              title="更新"
                              onClick={() =>
                                doIt(
                                  () =>
                                    api.post<string>(
                                      `/api/clients/${encodeURIComponent(c.clientId)}/trigger-update`,
                                    ),
                                  "已触发更新检查",
                                )
                              }
                            >
                              <UploadCloud className="h-4 w-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              title="部署命令"
                              onClick={() => setDeploy(c)}
                            >
                              <Rocket className="h-4 w-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              title="卸载客户端"
                              onClick={() => setUninstall(c)}
                            >
                              <Trash2 className="h-4 w-4 text-danger" />
                            </Button>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </CardBody>
      </Card>
      <EditModal
        client={edit}
        onClose={() => setEdit(null)}
        onSaved={() => load(false)}
      />
      <DeployModal
        client={deploy}
        onClose={() => setDeploy(null)}
        onChanged={() => load(false)}
      />
      <UninstallModal
        client={uninstall}
        onClose={() => setUninstall(null)}
        onDeleted={() => load(false)}
      />
      <ClientLogModal client={logClient} onClose={() => setLogClient(null)} />
    </div>
  );
}

function isClientOnline(client: ClientInfo): boolean {
  return client.isOnline;
}

function EditModal({
  client,
  onClose,
  onSaved,
}: {
  client: ClientInfo | null;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const toast = useToast();
  const [name, setName] = useState("");
  const [isp, setIsp] = useState<IspKey>("Telecom");
  useEffect(() => {
    if (client) {
      setName(client.name || "");
      setIsp(ispKey(client.isp));
    }
  }, [client]);
  async function save() {
    if (!client) return;
    await api.post<string>(
      `/api/clients/${encodeURIComponent(client.clientId)}/metadata`,
      { name, isp },
    );
    toast("客户端信息已更新", "success");
    onClose();
    await onSaved();
  }
  return (
    <Modal
      open={!!client}
      title="编辑客户端"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            取消
          </Button>
          <Button onClick={save}>保存</Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label="客户端名称">
          <Input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="运营商">
          <Select
            value={isp}
            onChange={(e) => setIsp(e.target.value as IspKey)}
          >
            {ISP_KEYS.map((k) => (
              <option key={k} value={k}>
                {ispLabel(k)}
              </option>
            ))}
          </Select>
        </Field>
      </div>
    </Modal>
  );
}

function ClientLogModal({
  client,
  onClose,
}: {
  client: ClientInfo | null;
  onClose: () => void;
}) {
  const log = client?.runtimeLog?.trim();
  return (
    <Modal
      open={!!client}
      title="客户端日志"
      onClose={onClose}
      maxWidth="max-w-4xl"
    >
      <div className="space-y-3">
        <div className="grid gap-2 text-sm sm:grid-cols-2">
          <div>
            <span className="text-fg-subtle">节点：</span>
            {client?.name || client?.clientId.slice(0, 8)}
          </div>
          <div>
            <span className="text-fg-subtle">运行：</span>
            {client?.runtimeStatus || "-"}
          </div>
          <div>
            <span className="text-fg-subtle">最后心跳：</span>
            {client?.lastSeenAt ? formatDateTime(client.lastSeenAt) : "-"}
          </div>
          <div>
            <span className="text-fg-subtle">版本：</span>
            {client?.version || "-"} / {client?.platform || "-"}
          </div>
        </div>
        {log ? (
          <Textarea
            readOnly
            value={log}
            className="min-h-[360px] font-mono text-xs"
          />
        ) : (
          <Empty title="暂无日志" desc="客户端下次心跳后会同步最近运行日志" />
        )}
      </div>
    </Modal>
  );
}

function UninstallModal({
  client,
  onClose,
  onDeleted,
}: {
  client: ClientInfo | null;
  onClose: () => void;
  onDeleted: () => Promise<void>;
}) {
  const toast = useToast();
  const [commands, setCommands] = useState<{
    linux: string;
    macos: string;
    windows: string;
  } | null>(null);
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    setCommands(null);
  }, [client]);
  async function loadCommands() {
    if (!client?.clientId) return;
    setLoading(true);
    try {
      const request = (platform: "linux" | "macos" | "windows") =>
        api.post<ClientInstallScriptResponse>("/api/client/install-script", {
          platform,
          scriptType: "uninstall",
          clientId: client.clientId,
        });
      const [linux, macos, windows] = await Promise.all([
        request("linux"),
        request("macos"),
        request("windows"),
      ]);
      setCommands({
        linux: linux.script,
        macos: macos.script,
        windows: windows.script,
      });
    } finally {
      setLoading(false);
    }
  }
  async function copy(value: string) {
    await navigator.clipboard.writeText(value);
    toast("已复制", "success");
  }
  async function deleteRecord() {
    if (!client) return;
    if (!confirm("确定删除该客户端记录？")) return;
    await api.del<string>(
      `/api/clients/${encodeURIComponent(client.clientId)}`,
    );
    toast("客户端已删除", "success");
    onClose();
    await onDeleted();
  }
  return (
    <Modal
      open={!!client}
      title="卸载客户端"
      onClose={onClose}
      maxWidth="max-w-3xl"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            关闭
          </Button>
          <Button variant="danger" onClick={deleteRecord}>
            删除客户端记录
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <div className="text-sm text-fg-muted">
          节点：{client?.name || client?.clientId.slice(0, 8)}
        </div>
        <Button onClick={loadCommands} disabled={loading}>
          {loading ? "获取中..." : "获取卸载命令"}
        </Button>
        {commands && (
          <div className="space-y-3">
            <CommandBlock
              title="Linux Bash"
              value={commands.linux}
              onCopy={() => copy(commands.linux)}
            />
            <CommandBlock
              title="macOS Bash"
              value={commands.macos}
              onCopy={() => copy(commands.macos)}
            />
            <CommandBlock
              title="Windows PowerShell"
              value={commands.windows}
              onCopy={() => copy(commands.windows)}
            />
          </div>
        )}
      </div>
    </Modal>
  );
}

function CommandBlock({
  title,
  value,
  onCopy,
}: {
  title: string;
  value: string;
  onCopy: () => void;
}) {
  return (
    <div>
      <div className="mb-2 flex items-center justify-between text-sm font-medium">
        {title}
        <Button variant="ghost" size="sm" onClick={onCopy}>
          <Copy className="h-4 w-4" />
          复制
        </Button>
      </div>
      <CodeBox value={value} />
    </div>
  );
}

function DeployModal({
  client,
  onClose,
  onChanged,
}: {
  client: ClientInfo | null;
  onClose: () => void;
  onChanged: () => Promise<void>;
}) {
  const toast = useToast();
  const [name, setName] = useState("");
  const [isp, setIsp] = useState<IspKey>("Telecom");
  const [serverUrl, setServerUrl] = useState(location.origin);
  const [platform, setPlatform] = useState<
    "linux" | "windows" | "macos" | "docker"
  >("linux");
  const [method, setMethod] = useState<"native" | "run" | "compose">("native");
  const [includeProxy, setIncludeProxy] = useState(true);
  const [disableAutoUpdate, setDisableAutoUpdate] = useState(false);
  const [dockerProxy, setDockerProxy] = useState("");
  const [res, setRes] = useState<BootstrapTokenCreateResponse | null>(null);
  const [status, setStatus] = useState<BootstrapTokenStatus | null>(null);
  useEffect(() => {
    if (client) {
      setName(client.name || "");
      setIsp(ispKey(client.isp));
      setRes(null);
      setStatus(null);
    }
  }, [client]);
  useEffect(() => {
    if (!res) return;
    const poll = () =>
      api
        .get<BootstrapTokenStatus>(
          `/api/bootstrap/${encodeURIComponent(res.token)}/status`,
          undefined,
          true,
        )
        .then((s) => {
          setStatus(s);
          if (s.consumed || s.online) onChanged().catch(() => {});
        })
        .catch(() => {});
    poll();
    const t = setInterval(poll, 2500);
    return () => clearInterval(t);
  }, [res]);
  async function create() {
    const r = await api.post<BootstrapTokenCreateResponse>(
      "/api/bootstrap/create",
      {
        name,
        isp,
        serverUrl,
        includeProxy: method === "native" && includeProxy,
        disableAutoUpdate,
        dockerProxy: platform === "docker" ? dockerProxy : "",
        clientId: client?.clientId || undefined,
      },
    );
    setRes(r);
    toast("部署命令已生成", "success");
    await onChanged();
  }
  async function copy(v: string) {
    await navigator.clipboard.writeText(v);
    toast("已复制", "success");
  }
  const container = res ? containerDeploy(res) : null;
  const stateTone = status?.online
    ? "success"
    : status?.consumed
      ? "primary"
      : method !== "run" && status?.expired
        ? "danger"
        : "warning";
  const stateText = status?.online
    ? "已上线"
    : status?.consumed
      ? "已添加"
      : method !== "run" && status?.expired
        ? "已过期"
        : "等待上线";
  return (
    <Modal
      open={!!client}
      title="一键部署指令"
      onClose={onClose}
      maxWidth="max-w-2xl"
      footer={
        <Button onClick={create}>
          <Rocket className="h-4 w-4" />
          生成命令
        </Button>
      }
    >
      <div className="space-y-4">
        <div
          role="group"
          aria-label="部署平台"
          className="grid grid-cols-4 gap-1 rounded-xl bg-surface-2 p-1"
        >
          {(
            [
              ["linux", "Linux"],
              ["windows", "Windows"],
              ["macos", "macOS"],
              ["docker", "Docker"],
            ] as const
          ).map(([key, label]) => (
            <button
              key={key}
              type="button"
              aria-pressed={platform === key}
              onClick={() => {
                if ((platform === "docker") !== (key === "docker")) {
                  setMethod(key === "docker" ? "run" : "native");
                  setRes(null);
                  setStatus(null);
                }
                setPlatform(key);
              }}
              className={`h-9 rounded-lg border px-1 text-sm font-medium transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-primary/50 ${
                platform === key
                  ? "border-border bg-card text-fg shadow-sm"
                  : "border-transparent text-fg-muted hover:text-fg"
              }`}
            >
              {label}
            </button>
          ))}
        </div>
        <div className="grid items-start gap-x-4 gap-y-5 sm:grid-cols-2">
          <Field label="客户端名称">
            <Input
              className="h-10"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="留空自动生成"
            />
          </Field>
          <Field label="运营商">
            <Select
              className="h-10"
              value={isp}
              onChange={(e) => setIsp(e.target.value as IspKey)}
            >
              {ISP_KEYS.map((k) => (
                <option key={k} value={k}>
                  {ispLabel(k)}
                </option>
              ))}
            </Select>
          </Field>
          <Field
            label="服务端地址"
            hint={
              method !== "native"
                ? "填写容器可访问的地址；localhost / 127.0.0.1 指向容器自身。"
                : undefined
            }
          >
            <Input
              className="h-10"
              value={serverUrl}
              onChange={(e) => setServerUrl(e.target.value)}
            />
          </Field>
          {platform === "docker" && (
            <Field label="部署方式">
              <Select
                className="h-10"
                value={method}
                onChange={(e) => setMethod(e.target.value as "run" | "compose")}
              >
                <option value="run">直接命令</option>
                <option value="compose">Compose</option>
              </Select>
            </Field>
          )}
          {platform === "docker" && (
            <div className="sm:col-span-2">
              <Field label="Docker 镜像代理（可选）" hint="填写代理域名，例如 docker.qwq.lu；留空直接从 GHCR 拉取。">
                <Input
                  className="h-10"
                  value={dockerProxy}
                  onChange={(e) => setDockerProxy(e.target.value)}
                  placeholder="docker.qwq.lu"
                />
              </Field>
            </div>
          )}
          <div className="grid gap-3 text-sm sm:col-span-2 sm:grid-cols-2">
            {method === "native" && (
              <label className="flex items-center justify-between rounded-lg border border-border p-3">
                携带 GH Proxy
                <Switch checked={includeProxy} onChange={setIncludeProxy} />
              </label>
            )}
            <label className={`flex items-center justify-between rounded-lg border border-border p-3 ${method !== "native" ? "sm:col-span-2" : ""}`}>
              禁用自动更新
              <Switch
                checked={disableAutoUpdate}
                onChange={setDisableAutoUpdate}
              />
            </label>
          </div>
        </div>
        {method !== "native" && (
          <p className="rounded-lg bg-surface-2 px-3 py-2.5 text-xs leading-relaxed text-fg-muted">
            容器内默认允许自动更新，需在服务端启用客户端更新。安装后由 Docker 重启容器；重建容器后使用镜像中的版本。
          </p>
        )}
        {res && (
          <div className="space-y-3 rounded-xl border border-border bg-surface p-4">
            <div className="flex flex-wrap gap-2 text-sm">
              <Badge tone="primary">
                {method === "native"
                  ? `Token ${res.token}`
                  : `客户端 ${res.clientId}`}
              </Badge>
              <Badge tone={stateTone}>{stateText}</Badge>
              {method !== "run" && (
                <span className="text-fg-muted">
                  过期时间：{formatDateTime(res.expiresAtUtc)}
                </span>
              )}
            </div>
            {method === "native" ? (
              <CommandBlock
                title={
                  platform === "windows"
                    ? "Windows PowerShell"
                    : platform === "macos"
                      ? "macOS"
                      : "Linux"
                }
                value={
                  platform === "windows" ? res.windowsCommand : res.linuxCommand
                }
                onCopy={() =>
                  copy(
                    platform === "windows"
                      ? res.windowsCommand
                      : res.linuxCommand,
                  )
                }
              />
            ) : container && method === "run" ? (
              <>
                <p className="text-sm text-fg-muted">
                  在客户端机器的 Linux / macOS shell 中执行以下命令。
                </p>
                <CommandBlock
                  title="Docker 运行命令"
                  value={container.run}
                  onCopy={() => copy(container.run)}
                />
              </>
            ) : (
              container && (
                <>
                  <p className="text-sm text-fg-muted">
                    在独立目录执行以下命令，自动下载 compose.yml 并启动客户端。
                    环境变量可直接在下载后的文件中修改。
                  </p>
                  <CommandBlock
                    title="Compose 一键部署"
                    value={container.start}
                    onCopy={() => copy(container.start)}
                  />
                  <CommandBlock
                    title="更新镜像"
                    value={container.update}
                    onCopy={() => copy(container.update)}
                  />
                </>
              )
            )}
            {status?.runtimeStatus && (
              <Textarea readOnly value={status.runtimeStatus} />
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}
