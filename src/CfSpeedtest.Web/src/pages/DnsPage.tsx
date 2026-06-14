import { useEffect, useState } from "react";
import { Play, RefreshCw, ShieldCheck, Zap } from "lucide-react";
import { api } from "@/lib/api";
import { formatDateTime, formatLatency, formatLossRate, formatSpeed } from "@/lib/format";
import { ISP_KEYS, ispLabel } from "@/lib/isp";
import type { DnsUpdateStatus, IspKey } from "@/lib/types";
import { Badge, Button, Card, CardBody, CardHeader, Empty, Select, useToast } from "@/components/ui";

export function DnsPage() {
  const toast = useToast();
  const [items, setItems] = useState<DnsUpdateStatus[]>([]);
  const [isp, setIsp] = useState<"" | IspKey>("");
  const [loading, setLoading] = useState(false);

  async function load() { setLoading(true); try { setItems(await api.get<DnsUpdateStatus[]>("/api/dns/status") ?? []); } finally { setLoading(false); } }
  useEffect(() => { load().catch(() => {}); }, []);
  async function act(fn: () => Promise<unknown>, msg: string) { await fn(); toast(msg, "success"); await load(); }
  const body = isp ? { isp } : {};

  return <Card><CardHeader title="DNS 更新" desc="查看华为云 DNS 更新结果，手动更新或触发一轮测速" action={<><Select value={isp} onChange={(e) => setIsp(e.target.value as "" | IspKey)}><option value="">全部运营商</option>{ISP_KEYS.map((k) => <option key={k} value={k}>{ispLabel(k)}</option>)}</Select><Button variant="secondary" onClick={load}><RefreshCw className="h-4 w-4" />刷新</Button><Button variant="secondary" onClick={() => act(() => api.post<string>("/api/dns/test-auth"), "凭证测试请求已完成")}><ShieldCheck className="h-4 w-4" />测试凭证</Button><Button variant="secondary" onClick={() => act(() => api.post<string>("/api/dns/test-record", body), "记录配置测试完成")}><Zap className="h-4 w-4" />测试记录</Button><Button onClick={() => act(() => api.post<DnsUpdateStatus[]>("/api/dns/update", body), "DNS 更新已触发")}>手动更新</Button><Button variant="secondary" onClick={() => act(() => api.post<string>("/api/dns/trigger-test", body), "已触发测速") }><Play className="h-4 w-4" />触发测速</Button></>} /><CardBody>{loading ? <div className="py-8 text-center text-sm text-fg-muted">加载中...</div> : !items.length ? <Empty title="暂无 DNS 状态" /> : <div className="grid gap-4 lg:grid-cols-3">{items.filter((x) => !isp || x.isp === isp).map((d) => <div key={d.isp} className="rounded-xl border border-border bg-surface p-4"><div className="mb-3 flex items-center justify-between"><Badge tone={d.success ? "success" : "danger"}>{ispLabel(d.isp)}</Badge><Badge tone={d.success ? "success" : "warning"}>{d.success ? "成功" : "异常"}</Badge></div><div className="font-medium text-fg">{d.domain || "未配置域名"}</div><div className="mt-1 text-xs text-fg-muted">更新时间：{formatDateTime(d.lastUpdatedAt)}</div><div className="mt-3 text-xs text-fg-subtle">{d.message || "-"}</div><div className="mt-4 space-y-2">{d.results.slice(0, 5).map((r) => <div key={r.ipAddress} className="rounded-lg bg-card px-3 py-2 text-sm"><div className="flex items-center justify-between gap-3"><span className="font-mono font-medium">{r.ipAddress}</span><span className="text-fg-muted">{formatSpeed(r.downloadSpeedKBps)}</span></div><div className="mt-2 grid grid-cols-3 gap-2 text-xs text-fg-subtle"><span>延迟 {formatLatency(r.avgLatencyMs)}</span><span>丢包 {formatLossRate(r.packetLossRate)}</span><span>评分 {r.score.toFixed(1)}</span></div></div>)}</div></div>)}</div>}</CardBody></Card>;
}
