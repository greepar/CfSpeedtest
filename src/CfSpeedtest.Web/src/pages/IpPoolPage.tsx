import { useEffect, useMemo, useState } from "react";
import { Plus, RefreshCw, Search, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import { ISP_KEYS, ispLabel } from "@/lib/isp";
import type { FetchSource, IpPoolMap, IspKey, ServerConfig } from "@/lib/types";
import {
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  CodeBox,
  Empty,
  Field,
  Modal,
  Select,
  Textarea,
  useToast,
} from "@/components/ui";

type SourceType = 0 | 1;
type InputMode = "manual" | "cname" | "api";

export function IpPoolPage() {
  const toast = useToast();
  const [pool, setPool] = useState<IpPoolMap>({});
  const [config, setConfig] = useState<ServerConfig | null>(null);
  const [isp, setIsp] = useState<IspKey>("Telecom");
  const [ips, setIps] = useState("");
  const [inputMode, setInputMode] = useState<InputMode>("manual");
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

  useEffect(() => {
    load().catch(() => {});
  }, []);

  const current = pool[isp] ??
    Object.entries(pool).find(
      ([k]) => k.toLowerCase() === isp.toLowerCase(),
    )?.[1] ?? { manualIps: [], apiIps: [], cnameIps: [], allIps: [] };
  const sourceConfig =
    config?.ipSources?.[isp] ??
    Object.entries(config?.ipSources ?? {}).find(
      ([k]) => k.toLowerCase() === isp.toLowerCase(),
    )?.[1];
  const sources = sourceConfig?.fetchSources ?? [];
  const inputIps = useMemo(
    () =>
      ips
        .split(/\s|,|;/)
        .map((x) => x.trim())
        .filter(Boolean),
    [ips],
  );
  const sourceType: SourceType = inputMode === "cname" ? 1 : 0;

  async function add() {
    await api.post<string>("/api/ippool/add", { isp, ips: inputIps });
    setIps("");
    toast("IP 已添加", "success");
    await load();
  }

  async function replace() {
    await api.post<string>("/api/ippool/replace", { isp, ips: inputIps });
    setReplaceOpen(false);
    toast("手动 IP 池已覆盖", "success");
    await load();
  }

  async function remove(ip: string, source: string) {
    await api.post<string>("/api/ippool/remove", { isp, ip, source });
    toast("IP 已删除", "success");
    await load();
  }

  async function removeSource(source: FetchSource) {
    const sourceLabel = Number(source.type) === 0 ? "API" : "CNAME";
    if (!confirm(`确定删除这个 ${sourceLabel} 拉取源？\n${source.value}`))
      return;

    await api.post<string>("/api/ippool/source/remove", source, { isp });
    toast("拉取源已删除", "success");
    await load();
  }

  async function saveSource() {
    const source: FetchSource = { type: sourceType, value: ips.trim() };
    await api.post<string>("/api/ippool/source/add", source, { isp });
    toast("拉取源已保存", "success");
    await load();
  }

  async function refresh() {
    await api.post<string>("/api/ippool/refresh", undefined, { isp });
    toast("已触发刷新", "success");
    await load();
  }

  async function doPreview() {
    const source: FetchSource = { type: sourceType, value: ips.trim() };
    setPreview(await api.post<string[]>("/api/ippool/preview", source));
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title="IP 池"
          desc="按运营商维护手动 IP、CNAME 解析和 API 拉取来源"
          action={
            <>
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
              <Button variant="secondary" onClick={load}>
                <RefreshCw className="h-4 w-4" />
                刷新
              </Button>
              <Button onClick={refresh}>刷新来源池</Button>
            </>
          }
        />
        <CardBody>
          <div className="grid gap-6 lg:grid-cols-[460px_1fr]">
            <div className="space-y-4">
              <div className="space-y-3">
                <div className="grid grid-cols-3 gap-2">
                  <Button
                    variant={inputMode === "manual" ? "primary" : "secondary"}
                    onClick={() => {
                      setInputMode("manual");
                      setPreview(null);
                    }}
                  >
                    手动 IP
                  </Button>
                  <Button
                    variant={inputMode === "cname" ? "primary" : "secondary"}
                    onClick={() => {
                      setInputMode("cname");
                      setPreview(null);
                    }}
                  >
                    CNAME 源
                  </Button>
                  <Button
                    variant={inputMode === "api" ? "primary" : "secondary"}
                    onClick={() => {
                      setInputMode("api");
                      setPreview(null);
                    }}
                  >
                    API 源
                  </Button>
                </div>
                <Field
                  label={
                    inputMode === "manual"
                      ? "手动 IP"
                      : inputMode === "cname"
                        ? "CNAME 域名"
                        : "API URL"
                  }
                >
                  <Textarea
                    value={ips}
                    onChange={(e) => setIps(e.target.value)}
                    placeholder={
                      inputMode === "manual"
                        ? "可输入多个 IP，以空格、逗号或换行分隔"
                        : inputMode === "cname"
                          ? "填写需要解析的 CNAME 域名"
                          : "填写返回 IP 列表的 API URL"
                    }
                  />
                </Field>
              </div>

              {sources.length > 0 && (
                <div className="rounded-xl border border-border bg-surface p-3">
                  <div className="mb-2 text-xs font-medium text-fg-muted">
                    配置里的拉取源
                  </div>
                  <div className="space-y-2">
                    {sources.map((s, i) => {
                      const sourceType = Number(s.type) as SourceType;
                      return (
                        <div
                          key={`${s.type}-${s.value}-${i}`}
                          className="flex items-center gap-2 rounded-lg bg-card px-3 py-2 text-sm hover:bg-card-hover"
                        >
                          <button
                            type="button"
                            onClick={() => {
                              setInputMode(sourceType === 0 ? "api" : "cname");
                              setIps(s.value);
                            }}
                            className="flex min-w-0 flex-1 items-center gap-2 text-left"
                          >
                            <Badge tone={sourceType === 0 ? "info" : "warning"}>
                              {sourceType === 0 ? "API" : "CNAME"}
                            </Badge>
                            <span className="min-w-0 truncate font-mono text-xs">
                              {s.value}
                            </span>
                          </button>
                          <Button
                            variant="ghost"
                            size="icon"
                            className="h-7 w-7 shrink-0"
                            onClick={() => removeSource(s)}
                            title="删除拉取源"
                          >
                            <Trash2 className="h-4 w-4 text-danger" />
                          </Button>
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}

              <div className="flex flex-wrap gap-2">
                {inputMode === "manual" ? (
                  <>
                    <Button onClick={add} disabled={!inputIps.length}>
                      <Plus className="h-4 w-4" />
                      添加手动 IP
                    </Button>
                    <Button
                      variant="secondary"
                      onClick={() => setReplaceOpen(true)}
                      disabled={!inputIps.length}
                    >
                      覆盖手动池
                    </Button>
                  </>
                ) : (
                  <>
                    <Button
                      variant="secondary"
                      onClick={doPreview}
                      disabled={!ips.trim()}
                    >
                      <Search className="h-4 w-4" />
                      预览来源
                    </Button>
                    <Button onClick={saveSource} disabled={!ips.trim()}>
                      <Plus className="h-4 w-4" />
                      保存{inputMode === "cname" ? " CNAME" : " API"}源
                    </Button>
                  </>
                )}
              </div>

              {preview && (
                <div>
                  <div className="mb-2 text-sm font-medium">
                    预览结果（{preview.length}）
                  </div>
                  <CodeBox value={preview.join("\n")} />
                </div>
              )}
            </div>

            <div className="space-y-4">
              <div className="grid gap-4 xl:grid-cols-3">
                <IpList
                  title="手动 IP"
                  source="manual"
                  ips={current.manualIps ?? []}
                  onRemove={remove}
                />
                <IpList
                  title="CNAME 源"
                  emptyTitle="暂无 CNAME 源"
                  source="cname"
                  ips={current.cnameIps ?? []}
                  onRemove={remove}
                />
                <IpList
                  title="API 源"
                  emptyTitle="暂无 API 源"
                  source="api"
                  ips={current.apiIps ?? []}
                  onRemove={remove}
                />
              </div>
              <IpList title="汇总 IP 池" ips={current.allIps ?? []} />
            </div>
          </div>
        </CardBody>
      </Card>

      <Modal
        open={replaceOpen}
        title="确认覆盖手动 IP 池"
        onClose={() => setReplaceOpen(false)}
        footer={
          <>
            <Button variant="secondary" onClick={() => setReplaceOpen(false)}>
              取消
            </Button>
            <Button variant="danger" onClick={replace}>
              确认覆盖
            </Button>
          </>
        }
      >
        <p className="text-sm text-fg-muted">
          这会用输入框中的 {inputIps.length} 个 IP 覆盖当前 {ispLabel(isp)}{" "}
          的手动 IP 池。
        </p>
      </Modal>
    </div>
  );
}

function IpList({
  title,
  emptyTitle = "暂无 IP",
  source,
  ips,
  onRemove,
}: {
  title: string;
  emptyTitle?: string;
  source?: string;
  ips: string[];
  onRemove?: (ip: string, source: string) => void;
}) {
  return (
    <div className="rounded-xl border border-border bg-surface p-4">
      <div className="mb-3 flex items-center justify-between">
        <h3 className="font-medium">{title}</h3>
        <Badge>{ips.length}</Badge>
      </div>
      {!ips.length ? (
        <Empty title={emptyTitle} />
      ) : (
        <div className="max-h-[520px] space-y-1 overflow-auto">
          {ips.map((ip) => (
            <div
              key={`${source ?? "all"}-${ip}`}
              className="flex items-center justify-between rounded-lg px-2 py-1.5 hover:bg-card-hover"
            >
              <span className="font-mono text-sm">{ip}</span>
              {onRemove && source && (
                <Button
                  variant="ghost"
                  size="icon"
                  className="h-7 w-7"
                  onClick={() => onRemove(ip, source)}
                >
                  <Trash2 className="h-4 w-4 text-danger" />
                </Button>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
