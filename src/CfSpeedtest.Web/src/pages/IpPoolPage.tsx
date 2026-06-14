import { useEffect, useMemo, useState } from "react";
import { Plus, RefreshCw, Search, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { ISP_KEYS, ispLabel } from "@/lib/isp";
import type { FetchSource, IpPoolMap, IspKey, ServerConfig } from "@/lib/types";
import { Badge, Button, Card, CardBody, CardHeader, CodeBox, Empty, Field, Modal, Select, Textarea, useToast } from "@/components/ui";

type SourceType = 0 | 1;

export function IpPoolPage() {
  const toast = useToast();
  const [pool, setPool] = useState<IpPoolMap>({});
  const [config, setConfig] = useState<ServerConfig | null>(null);
  const [isp, setIsp] = useState<IspKey>("Telecom");
  const [ips, setIps] = useState("");
  const [previewType, setPreviewType] = useState<SourceType>(0);
  const [replaceOpen, setReplaceOpen] = useState(false);
  const [preview, setPreview] = useState<string[] | null>(null);

  async function load() {
    const [poolData, cfg] = await Promise.all([
      api.get<IpPoolMap>("/api/ippool"),
      api.get<ServerConfig>("/api/config"),
    ]);
    setPool(poolData ?? {});
    setConfig(cfg ?? null);
  }
  useEffect(() => { load().catch(() => {}); }, []);

  const current = pool[isp] ?? Object.entries(pool).find(([k]) => k.toLowerCase() === isp.toLowerCase())?.[1] ?? { manualIps: [], apiIps: [] };
  const sourceConfig = config?.ipSources?.[isp] ?? Object.entries(config?.ipSources ?? {}).find(([k]) => k.toLowerCase() === isp.toLowerCase())?.[1];
  const sources = sourceConfig?.fetchSources ?? [];
  const inputIps = useMemo(() => ips.split(/\s|,|;/).map((x) => x.trim()).filter(Boolean), [ips]);

  async function add() { await api.post<string>("/api/ippool/add", { isp, ips: inputIps }); setIps(""); toast("IP 已添加", "success"); await load(); }
  async function replace() { await api.post<string>("/api/ippool/replace", { isp, ips: inputIps }); setReplaceOpen(false); toast("手动 IP 池已覆盖", "success"); await load(); }
  async function remove(ip: string, source: string) { await api.post<string>("/api/ippool/remove", { isp, ip, source }); toast("IP 已删除", "success"); await load(); }
  async function refresh() { await api.post<string>("/api/ippool/refresh", undefined, { isp }); toast("已触发刷新", "success"); await load(); }
  async function doPreview() {
    const source: FetchSource = { type: previewType, value: ips.trim() };
    setPreview(await api.post<string[]>("/api/ippool/preview", source));
  }

  return <div className="space-y-6"><Card><CardHeader title="IP 池" desc="按运营商维护手动 IP 与 API 拉取 IP" action={<><Select value={isp} onChange={(e) => setIsp(e.target.value as IspKey)}>{ISP_KEYS.map((k) => <option key={k} value={k}>{ispLabel(k)}</option>)}</Select><Button variant="secondary" onClick={load}><RefreshCw className="h-4 w-4" />刷新</Button><Button onClick={refresh}>拉取 API 池</Button></>} /><CardBody><div className="grid gap-6 lg:grid-cols-[460px_1fr]"><div className="space-y-4"><div className="grid gap-3 sm:grid-cols-[150px_1fr]"><Field label="预览源类型"><Select value={previewType} onChange={(e) => setPreviewType(Number(e.target.value) as SourceType)}><option value={0}>API 源</option><option value={1}>CNAME 源</option></Select></Field><Field label="添加 IP / 预览源"><Textarea value={ips} onChange={(e) => setIps(e.target.value)} placeholder={previewType === 0 ? "填写 API URL；添加 IP 时可输入多个 IP" : "填写 CNAME 域名；添加 IP 时可输入多个 IP"} /></Field></div>{sources.length > 0 && <div className="rounded-xl border border-border bg-surface p-3"><div className="mb-2 text-xs font-medium text-fg-muted">配置里的拉取源</div><div className="space-y-2">{sources.map((s, i) => <button key={`${s.type}-${s.value}-${i}`} type="button" onClick={() => { setPreviewType(Number(s.type) as SourceType); setIps(s.value); }} className="flex w-full items-center gap-2 rounded-lg bg-card px-3 py-2 text-left text-sm hover:bg-card-hover"><Badge tone={Number(s.type) === 0 ? "info" : "warning"}>{Number(s.type) === 0 ? "API" : "CNAME"}</Badge><span className="min-w-0 truncate font-mono text-xs">{s.value}</span></button>)}</div></div>}<div className="flex flex-wrap gap-2"><Button onClick={add} disabled={!inputIps.length}><Plus className="h-4 w-4" />添加</Button><Button variant="secondary" onClick={() => setReplaceOpen(true)} disabled={!inputIps.length}>覆盖手动池</Button><Button variant="secondary" onClick={doPreview} disabled={!ips.trim()}><Search className="h-4 w-4" />预览源</Button></div>{preview && <div><div className="mb-2 text-sm font-medium">预览结果（{preview.length}）</div><CodeBox value={preview.join("\n")} /></div>}</div><div className="grid gap-4 xl:grid-cols-2"><IpList title="手动 IP" source="manual" ips={current.manualIps} onRemove={remove} /><IpList title="API IP" source="api" ips={current.apiIps} onRemove={remove} /></div></div></CardBody></Card><Modal open={replaceOpen} title="确认覆盖手动 IP 池" onClose={() => setReplaceOpen(false)} footer={<><Button variant="secondary" onClick={() => setReplaceOpen(false)}>取消</Button><Button variant="danger" onClick={replace}>确认覆盖</Button></>}><p className="text-sm text-fg-muted">这会用输入框中的 {inputIps.length} 个 IP 覆盖当前 {ispLabel(isp)} 的手动 IP 池。</p></Modal></div>;
}

function IpList({ title, source, ips, onRemove }: { title: string; source: string; ips: string[]; onRemove: (ip: string, source: string) => void }) {
  return <div className="rounded-xl border border-border bg-surface p-4"><div className="mb-3 flex items-center justify-between"><h3 className="font-medium">{title}</h3><Badge>{ips.length}</Badge></div>{!ips.length ? <Empty title="暂无 IP" /> : <div className="max-h-[520px] space-y-1 overflow-auto">{ips.map((ip) => <div key={`${source}-${ip}`} className="flex items-center justify-between rounded-lg px-2 py-1.5 hover:bg-card-hover"><span className="font-mono text-sm">{ip}</span><Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => onRemove(ip, source)}><Trash2 className="h-4 w-4 text-danger" /></Button></div>)}</div>}</div>;
}
