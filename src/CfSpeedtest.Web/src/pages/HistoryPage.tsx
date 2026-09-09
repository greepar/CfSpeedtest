import { useEffect, useState } from "react";
import { RefreshCw, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import {
  formatDateTime,
  formatLatency,
  formatLossRate,
  formatSpeed,
} from "@/lib/format";
import { ispBadgeTone, ispLabel } from "@/lib/isp";
import type {
  HistoryTimeSegment,
  ServerConfig,
  TestHistory,
} from "@/lib/types";
import {
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  Empty,
  Field,
  Input,
  Progress,
  Select,
  Switch,
  useToast,
} from "@/components/ui";

export function HistoryPage() {
  const toast = useToast();
  const [items, setItems] = useState<TestHistory[]>([]);
  const [segments, setSegments] = useState<HistoryTimeSegment[]>([]);
  const [segment, setSegment] = useState("");
  const [loading, setLoading] = useState(false);
  const [config, setConfig] = useState<ServerConfig | null>(null);
  const [savingRetention, setSavingRetention] = useState(false);

  async function load() {
    setLoading(true);
    try {
      const segs = await api.get<HistoryTimeSegment[]>("/api/history/segments");
      setSegments(segs ?? []);
      const selected = segs?.find((s) => s.label === segment);
      const data = await api.get<TestHistory[]>(
        "/api/history",
        selected
          ? { from: selected.from, to: selected.to, limit: 500 }
          : { limit: 100 },
      );
      setItems(data ?? []);
    } finally {
      setLoading(false);
    }
  }
  useEffect(() => {
    load().catch(() => {});
  }, [segment]);
  useEffect(() => {
    api
      .get<ServerConfig>("/api/config")
      .then(setConfig)
      .catch(() => {});
  }, []);

  async function action(fn: () => Promise<unknown>, msg: string) {
    await fn();
    toast(msg, "success");
    await load();
  }

  async function saveRetention() {
    if (!config) return;
    setSavingRetention(true);
    try {
      await api.post<string>("/api/config", config);
      toast(
        config.historyRetentionDays > 0
          ? "自动清理设置已保存"
          : "已关闭自动清理",
        "success",
      );
    } finally {
      setSavingRetention(false);
    }
  }

  return (
    <Card>
      <CardHeader
        title="测速记录"
        desc="查看历史 TopN 结果、按时间段筛选和清理"
        action={
          <>
            <Select
              value={segment}
              onChange={(e) => setSegment(e.target.value)}
              className="w-56"
            >
              <option value="">最近 100 条</option>
              {segments.map((s) => (
                <option key={s.label} value={s.label}>
                  {s.label} ({s.count})
                </option>
              ))}
            </Select>
            <Button variant="secondary" onClick={() => load()}>
              <RefreshCw className="h-4 w-4" />
              刷新
            </Button>
            <Button
              variant="secondary"
              onClick={() =>
                action(
                  () => api.post<string>("/api/history/cleanup"),
                  "已清理过期记录",
                )
              }
            >
              清理过期
            </Button>
            <Button
              variant="danger"
              onClick={() =>
                confirm("确定清空全部测速记录？") &&
                action(
                  () => api.post<string>("/api/history/clear"),
                  "已清空记录",
                )
              }
            >
              清空
            </Button>
          </>
        }
      />
      <CardBody className="space-y-6">
        {config && (
          <div className="flex flex-col gap-3 rounded-xl border border-border bg-surface p-4 md:flex-row md:items-end md:justify-between">
            <div className="flex flex-1 flex-col gap-3 sm:flex-row sm:items-end">
              <div className="w-full sm:max-w-56">
                <Field label="保留天数">
                  <Input
                    type="number"
                    min="1"
                    disabled={config.historyRetentionDays <= 0}
                    value={
                      config.historyRetentionDays > 0
                        ? config.historyRetentionDays
                        : ""
                    }
                    onChange={(e) =>
                      setConfig({
                        ...config,
                        historyRetentionDays: Math.max(
                          1,
                          Number(e.target.value) || 1,
                        ),
                      })
                    }
                  />
                </Field>
              </div>
              <div className="pb-1 text-xs text-fg-subtle">
                超过保留期限的测速记录会在每次新增测速记录时自动清理。
              </div>
            </div>
            <div className="flex items-center gap-3">
              <span className="text-sm text-fg-muted">启用自动清理</span>
              <Switch
                checked={config.historyRetentionDays > 0}
                onChange={(enabled) =>
                  setConfig({
                    ...config,
                    historyRetentionDays: enabled ? 30 : 0,
                  })
                }
              />
              <Button loading={savingRetention} onClick={saveRetention}>
                保存设置
              </Button>
            </div>
          </div>
        )}
        {loading ? (
          <div className="py-8 text-center text-sm text-fg-muted">
            加载中...
          </div>
        ) : !items.length ? (
          <Empty title="暂无测速记录" />
        ) : (
          <div className="space-y-4">
            {items.map((h) => (
              <div
                key={h.id}
                className="rounded-xl border border-border bg-surface p-4"
              >
                <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge tone={ispBadgeTone(h.isp)}>{ispLabel(h.isp)}</Badge>
                    <span className="font-mono text-xs text-fg-muted">
                      {h.clientId}
                    </span>
                    <span className="text-xs text-fg-subtle">
                      {formatDateTime(h.completedAt)}
                    </span>
                  </div>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() =>
                      action(
                        () =>
                          api.del<string>(
                            `/api/history/${encodeURIComponent(h.id)}`,
                          ),
                        "记录已删除",
                      )
                    }
                  >
                    <Trash2 className="h-4 w-4" />
                    删除
                  </Button>
                </div>
                <div className="overflow-auto">
                  <table className="w-full min-w-[780px] text-sm">
                    <thead className="text-left text-xs text-fg-subtle">
                      <tr>
                        <th className="pb-2">IP</th>
                        <th>下载速度</th>
                        <th>平均延迟</th>
                        <th>最低延迟</th>
                        <th>丢包</th>
                        <th>TCP</th>
                        <th>评分</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-border">
                      {h.results.map((r) => (
                        <tr key={r.ipAddress}>
                          <td className="py-2 font-mono">{r.ipAddress}</td>
                          <td className="w-52">
                            <div className="flex items-center gap-2">
                              <Progress
                                value={Math.min(
                                  100,
                                  (r.downloadSpeedKBps / 1024) * 30,
                                )}
                                className="flex-1"
                              />
                              <span className="w-24 text-right">
                                {formatSpeed(r.downloadSpeedKBps)}
                              </span>
                            </div>
                          </td>
                          <td>{formatLatency(r.avgLatencyMs)}</td>
                          <td>{formatLatency(r.minLatencyMs)}</td>
                          <td>{formatLossRate(r.packetLossRate)}</td>
                          <td>
                            {r.tcpSuccessCount}/{r.tcpTotalCount}
                          </td>
                          <td>{r.score.toFixed(1)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            ))}
          </div>
        )}
      </CardBody>
    </Card>
  );
}
