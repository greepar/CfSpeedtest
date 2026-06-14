import { useEffect, useState } from "react";
import { Copy, Edit3, Play, RefreshCw, Rocket, Trash2, UploadCloud } from "lucide-react";
import { api } from "@/lib/api";
import { formatDateTime, parseUtc, timeAgo } from "@/lib/format";
import { ISP_KEYS, ispBadgeTone, ispKey, ispLabel } from "@/lib/isp";
import type { BootstrapTokenCreateResponse, BootstrapTokenStatus, ClientInfo, IspKey } from "@/lib/types";
import { Badge, Button, Card, CardBody, CardHeader, CodeBox, Empty, Field, Input, Modal, Progress, Select, Switch, Textarea, useToast } from "@/components/ui";

export function ClientsPage() {
  const toast = useToast();
  const [items, setItems] = useState<ClientInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [edit, setEdit] = useState<ClientInfo | null>(null);
  const [deploy, setDeploy] = useState<ClientInfo | null>(null);

  async function load(showLoading = false) {
    if (showLoading || items.length === 0) setLoading(true);
    try {
      setItems(await api.get<ClientInfo[]>("/api/clients") ?? []);
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
              <Button variant="secondary" onClick={() => load(true)}><RefreshCw className="h-4 w-4" />刷新</Button>
              <Button onClick={() => setDeploy({ clientId: "", isp: 0, name: "", isOnline: false, allowed: true, registeredAt: "", lastSeenAt: "", currentTaskTestedIps: 0, currentTaskTotalIps: 0 })}><Rocket className="h-4 w-4" />一键部署</Button>
            </>
          }
        />
        <CardBody>
          {loading && !items.length ? <div className="py-8 text-center text-sm text-fg-muted">加载中...</div> : !items.length ? <Empty title="暂无客户端" desc="点击一键部署创建第一个节点" /> : (
            <div className="overflow-auto">
              <table className="w-full min-w-[980px] text-sm">
                <thead className="text-left text-xs text-fg-subtle"><tr><th className="pb-3">节点</th><th>状态</th><th>运行</th><th>任务</th><th>版本/平台</th><th>最后心跳</th><th className="text-right">操作</th></tr></thead>
                <tbody className="divide-y divide-border">
                  {items.map((c) => {
                    const pct = c.currentTaskTotalIps ? c.currentTaskTestedIps / c.currentTaskTotalIps * 100 : 0;
                    const online = isClientOnline(c);
                    return (
                      <tr key={c.clientId}>
                        <td className="py-3"><div className="font-medium text-fg">{c.name || c.clientId.slice(0, 8)}</div><div className="mt-1 flex items-center gap-2"><Badge tone={ispBadgeTone(c.isp)}>{ispLabel(c.isp)}</Badge><span className="font-mono text-xs text-fg-subtle">{c.clientId}</span></div></td>
                        <td><div className="flex items-center gap-2"><Badge tone={online ? "success" : "danger"}>{online ? "在线" : "离线"}</Badge><Switch checked={c.allowed} onChange={(v) => doIt(() => api.post<string>(`/api/clients/${encodeURIComponent(c.clientId)}/allow`, undefined, { allowed: v }), v ? "已允许连接" : "已禁用连接")} /></div></td>
                        <td className="max-w-48 truncate text-fg-muted">{c.runtimeStatus || "-"}</td>
                        <td className="w-44"><Progress value={pct} /><div className="mt-1 text-xs text-fg-subtle">{c.currentTaskTestedIps}/{c.currentTaskTotalIps}</div></td>
                        <td><div>{c.version || "-"}</div><div className="text-xs text-fg-subtle">{c.platform || "-"}</div></td>
                        <td><div>{timeAgo(c.lastSeenAt)}</div><div className="text-xs text-fg-subtle">{formatDateTime(c.lastSeenAt)}</div></td>
                        <td><div className="flex justify-end gap-1"><Button variant="ghost" size="icon" title="编辑" onClick={() => setEdit(c)}><Edit3 className="h-4 w-4" /></Button><Button variant="ghost" size="icon" title="测速" onClick={() => doIt(() => api.post<string>(`/api/clients/${encodeURIComponent(c.clientId)}/trigger-test`), "已触发测速")}><Play className="h-4 w-4" /></Button><Button variant="ghost" size="icon" title="更新" onClick={() => doIt(() => api.post<string>(`/api/clients/${encodeURIComponent(c.clientId)}/trigger-update`), "已触发更新检查")}><UploadCloud className="h-4 w-4" /></Button><Button variant="ghost" size="icon" title="部署命令" onClick={() => setDeploy(c)}><Rocket className="h-4 w-4" /></Button><Button variant="ghost" size="icon" title="删除" onClick={() => confirm("确定删除该客户端？") && doIt(() => api.del<string>(`/api/clients/${encodeURIComponent(c.clientId)}`), "客户端已删除")}><Trash2 className="h-4 w-4 text-danger" /></Button></div></td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </CardBody>
      </Card>
      <EditModal client={edit} onClose={() => setEdit(null)} onSaved={() => load(false)} />
      <DeployModal client={deploy} onClose={() => setDeploy(null)} onChanged={() => load(false)} />
    </div>
  );
}

function isClientOnline(client: ClientInfo): boolean {
  if (!client.allowed || !client.isOnline || !client.lastSeenAt) return false;
  const lastSeen = parseUtc(client.lastSeenAt).getTime();
  if (!Number.isFinite(lastSeen) || lastSeen <= 0) return false;
  return Date.now() - lastSeen <= 3 * 60 * 1000;
}

function EditModal({ client, onClose, onSaved }: { client: ClientInfo | null; onClose: () => void; onSaved: () => Promise<void> }) {
  const toast = useToast(); const [name, setName] = useState(""); const [isp, setIsp] = useState<IspKey>("Telecom");
  useEffect(() => { if (client) { setName(client.name || ""); setIsp(ispKey(client.isp)); } }, [client]);
  async function save() { if (!client) return; await api.post<string>(`/api/clients/${encodeURIComponent(client.clientId)}/metadata`, { name, isp }); toast("客户端信息已更新", "success"); onClose(); await onSaved(); }
  return <Modal open={!!client} title="编辑客户端" onClose={onClose} footer={<><Button variant="secondary" onClick={onClose}>取消</Button><Button onClick={save}>保存</Button></>}><div className="grid gap-4"><Field label="客户端名称"><Input value={name} onChange={(e) => setName(e.target.value)} /></Field><Field label="运营商"><Select value={isp} onChange={(e) => setIsp(e.target.value as IspKey)}>{ISP_KEYS.map((k) => <option key={k} value={k}>{ispLabel(k)}</option>)}</Select></Field></div></Modal>;
}

function DeployModal({ client, onClose, onChanged }: { client: ClientInfo | null; onClose: () => void; onChanged: () => Promise<void> }) {
  const toast = useToast(); const [name, setName] = useState(""); const [isp, setIsp] = useState<IspKey>("Telecom"); const [serverUrl, setServerUrl] = useState(location.origin); const [includeProxy, setIncludeProxy] = useState(true); const [disableAutoUpdate, setDisableAutoUpdate] = useState(false); const [res, setRes] = useState<BootstrapTokenCreateResponse | null>(null); const [status, setStatus] = useState<BootstrapTokenStatus | null>(null);
  useEffect(() => { if (client) { setName(client.name || ""); setIsp(ispKey(client.isp)); setRes(null); setStatus(null); } }, [client]);
  useEffect(() => { if (!res) return; const poll = () => api.get<BootstrapTokenStatus>(`/api/bootstrap/${encodeURIComponent(res.token)}/status`, undefined, true).then((s) => { setStatus(s); if (s.consumed || s.online) onChanged().catch(() => {}); }).catch(() => {}); poll(); const t = setInterval(poll, 2500); return () => clearInterval(t); }, [res]);
  async function create() { const r = await api.post<BootstrapTokenCreateResponse>("/api/bootstrap/create", { name, isp, serverUrl, includeProxy, disableAutoUpdate, clientId: client?.clientId || undefined }); setRes(r); toast("部署命令已生成", "success"); await onChanged(); }
  async function copy(v: string) { await navigator.clipboard.writeText(v); toast("已复制命令", "success"); }
  const stateTone = status?.online ? "success" : status?.consumed ? "primary" : status?.expired ? "danger" : "warning";
  const stateText = status?.online ? "已上线" : status?.consumed ? "已添加" : status?.expired ? "已过期" : "等待上线";
  return <Modal open={!!client} title="一键部署客户端" onClose={onClose} maxWidth="max-w-3xl"><div className="space-y-4"><div className="grid gap-4 sm:grid-cols-2"><Field label="客户端名称"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="留空自动生成" /></Field><Field label="运营商"><Select value={isp} onChange={(e) => setIsp(e.target.value as IspKey)}>{ISP_KEYS.map((k) => <option key={k} value={k}>{ispLabel(k)}</option>)}</Select></Field><Field label="服务端地址"><Input value={serverUrl} onChange={(e) => setServerUrl(e.target.value)} /></Field><div className="grid gap-3 text-sm"><label className="flex items-center justify-between rounded-lg border border-border p-3">携带 GH Proxy<Switch checked={includeProxy} onChange={setIncludeProxy} /></label><label className="flex items-center justify-between rounded-lg border border-border p-3">禁用自动更新<Switch checked={disableAutoUpdate} onChange={setDisableAutoUpdate} /></label></div></div><Button onClick={create}><Rocket className="h-4 w-4" />生成命令</Button>{res && <div className="space-y-3 rounded-xl border border-border bg-surface p-4"><div className="flex flex-wrap gap-2 text-sm"><Badge tone="primary">Token {res.token}</Badge><Badge tone={stateTone}>{stateText}</Badge><span className="text-fg-muted">过期时间：{formatDateTime(res.expiresAtUtc)}</span></div><div><div className="mb-2 flex items-center justify-between text-sm font-medium">Linux / macOS<Button variant="ghost" size="sm" onClick={() => copy(res.linuxCommand)}><Copy className="h-4 w-4" />复制</Button></div><CodeBox value={res.linuxCommand} /></div><div><div className="mb-2 flex items-center justify-between text-sm font-medium">Windows PowerShell<Button variant="ghost" size="sm" onClick={() => copy(res.windowsCommand)}><Copy className="h-4 w-4" />复制</Button></div><CodeBox value={res.windowsCommand} /></div>{status?.runtimeStatus && <Textarea readOnly value={status.runtimeStatus} />}</div>}</div></Modal>;
}
