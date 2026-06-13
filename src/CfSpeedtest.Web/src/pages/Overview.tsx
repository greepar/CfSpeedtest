import { useEffect, useMemo, useState } from "react";
import type { ComponentType } from "react";
import { Database, Gauge, History, Users } from "lucide-react";
import { api } from "@/lib/api";
import { countdownTo, formatDateTime, formatNumber, formatSpeed, timeAgo } from "@/lib/format";
import { ispBadgeTone, ispLabel } from "@/lib/isp";
import type { ClientInfo, IpPoolMap, RoundStatusOverview, TestHistory } from "@/lib/types";
import { Badge, Card, CardBody, CardHeader, Empty, Progress, Spinner } from "@/components/ui";

export function Overview() {
  const [clients, setClients] = useState<ClientInfo[]>([]);
  const [history, setHistory] = useState<TestHistory[]>([]);
  const [rounds, setRounds] = useState<RoundStatusOverview | null>(null);
  const [pool, setPool] = useState<IpPoolMap>({});
  const [loading, setLoading] = useState(true);
  const [now, setNow] = useState(Date.now());

  async function load() {
    const [c, h, r, p] = await Promise.all([
      api.get<ClientInfo[]>("/api/clients"),
      api.get<TestHistory[]>("/api/history", { limit: 10 }),
      api.get<RoundStatusOverview>("/api/rounds/status"),
      api.get<IpPoolMap>("/api/ippool"),
    ]);
    setClients(c ?? []);
    setHistory(h ?? []);
    setRounds(r);
    setPool(p ?? {});
    setLoading(false);
  }

  useEffect(() => {
    load().catch(() => setLoading(false));
    const t = window.setInterval(() => load().catch(() => {}), 10000);
    const clock = window.setInterval(() => setNow(Date.now()), 1000);
    return () => { window.clearInterval(t); window.clearInterval(clock); };
  }, []);

  const poolCount = useMemo(() => Object.values(pool).reduce((n, v) => n + v.manualIps.length + v.apiIps.length, 0), [pool]);
  const online = clients.filter((c) => c.isOnline).length;
  const latest = history[0];

  if (loading) return <Spinner />;
  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <Stat icon={Users} label="客户端总数" value={clients.length} hint={`在线 ${online} 台`} />
        <Stat icon={Gauge} label="在线客户端" value={online} hint="最近心跳活跃" />
        <Stat icon={Database} label="IP 池总量" value={poolCount} hint="手动 + API 池" />
        <Stat icon={History} label="最近测速" value={history.length ? "已完成" : "暂无"} hint={latest ? timeAgo(latest.completedAt) : "等待上报"} />
      </div>
      <Card>
        <CardHeader title="统一轮次状态" desc={rounds ? `下一轮开始：${formatDateTime(rounds.nextRoundStartUtc)}，间隔 ${rounds.clientIntervalMinutes} 分钟` : "暂无轮次数据"} />
        <CardBody>
          {!rounds?.isps?.length ? <Empty title="暂无轮次状态" /> : <div className="grid gap-4 lg:grid-cols-3">{rounds.isps.map((r) => {
            const total = Math.max(1, r.assignedClients);
            const pct = (r.reportedClients / total) * 100;
            return <div key={r.isp} className="rounded-xl border border-border bg-surface p-4"><div className="mb-3 flex items-center justify-between"><Badge tone={ispBadgeTone(r.isp === "Telecom" ? 0 : r.isp === "Unicom" ? 1 : 2)}>{ispLabel(r.isp)}</Badge><Badge tone={r.finalized ? "success" : r.finalizing ? "warning" : "default"}>{r.finalized ? "已完成" : r.finalizing ? "汇总中" : "等待中"}</Badge></div><div className="font-mono text-lg font-semibold text-fg">{countdownTo(r.scheduledAtUtc, now)}</div><div className="mt-1 text-xs text-fg-subtle">计划开始倒计时</div><Progress className="mt-4" value={pct} /><div className="mt-2 text-xs text-fg-muted">已上报 {r.reportedClients} / 已分配 {r.assignedClients}</div></div>;
          })}</div>}
        </CardBody>
      </Card>
      <Card>
        <CardHeader title="最新测速结果" desc="最近上报的 TopN IP 结果" />
        <CardBody className="overflow-auto">
          {!history.length ? <Empty title="暂无测速记录" /> : <table className="w-full min-w-[760px] text-sm"><thead className="text-left text-xs text-fg-subtle"><tr><th className="pb-3">时间</th><th>客户端</th><th>运营商</th><th>最快 IP</th><th>速度</th><th>延迟</th></tr></thead><tbody className="divide-y divide-border">{history.map((h) => { const best = h.results?.[0]; return <tr key={h.id}><td className="py-3 text-fg-muted">{formatDateTime(h.completedAt)}</td><td className="font-mono text-xs">{h.clientId.slice(0, 10)}...</td><td><Badge tone={ispBadgeTone(h.isp)}>{ispLabel(h.isp)}</Badge></td><td className="font-mono">{best?.ipAddress ?? "-"}</td><td>{best ? formatSpeed(best.downloadSpeedKBps) : "-"}</td><td>{best ? `${best.avgLatencyMs.toFixed(1)} ms` : "-"}</td></tr>; })}</tbody></table>}
        </CardBody>
      </Card>
    </div>
  );
}

function Stat({ icon: Icon, label, value, hint }: { icon: ComponentType<{ className?: string }>; label: string; value: string | number; hint: string }) {
  return <Card><CardBody><div className="flex items-center justify-between"><div><div className="text-sm text-fg-muted">{label}</div><div className="mt-2 text-3xl font-semibold text-fg">{typeof value === "number" ? formatNumber(value) : value}</div><div className="mt-1 text-xs text-fg-subtle">{hint}</div></div><div className="grid h-11 w-11 place-items-center rounded-xl bg-primary-soft text-primary"><Icon className="h-5 w-5" /></div></div></CardBody></Card>;
}
