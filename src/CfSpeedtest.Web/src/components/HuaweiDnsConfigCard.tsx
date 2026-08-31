import { useEffect, useState } from "react";
import { Save } from "lucide-react";
import { api } from "@/lib/api";
import { ISP_KEYS, ispLabel } from "@/lib/isp";
import type { HuaweiDnsRecordConfig, IspKey, ServerConfig } from "@/lib/types";
import { Button, Card, CardBody, CardHeader, Field, Input, Switch, useToast } from "@/components/ui";

export function HuaweiDnsConfigCard() {
  const toast = useToast();
  const [cfg, setCfg] = useState<ServerConfig | null>(null);
  const [saving, setSaving] = useState(false);
  useEffect(() => { api.get<ServerConfig>("/api/config").then(setCfg).catch(() => {}); }, []);
  if (!cfg) return null;
  const setDns = (patch: Partial<ServerConfig["huaweiDns"]>) => setCfg({ ...cfg, huaweiDns: { ...cfg.huaweiDns, ...patch } });
  const setRecord = (key: IspKey, patch: Partial<HuaweiDnsRecordConfig>) => {
    const current = cfg.huaweiDns.records?.[key] ?? { zoneId: "", recordSetId: "", domain: "", ttl: 60 };
    setCfg({ ...cfg, huaweiDns: { ...cfg.huaweiDns, records: { ...cfg.huaweiDns.records, [key]: { ...current, ...patch } } } });
  };
  async function save() { setSaving(true); try { await api.post<string>("/api/config", cfg); toast("DNS 配置已保存", "success"); } finally { setSaving(false); } }
  return <Card><CardHeader title="华为云 DNS" desc="凭证和各运营商记录集" action={<Button loading={saving} onClick={save}><Save className="h-4 w-4" />保存配置</Button>} /><CardBody className="grid gap-4 md:grid-cols-2 xl:grid-cols-3"><Toggle label="启用 DNS 自动更新" checked={cfg.huaweiDns.enabled} onChange={(v) => setDns({ enabled: v })} /><Field label="Endpoint"><Input value={cfg.huaweiDns.endpoint} onChange={(e) => setDns({ endpoint: e.target.value })} /></Field><Num label="更新间隔（分钟）" value={cfg.huaweiDns.updateIntervalMinutes} onChange={(v) => setDns({ updateIntervalMinutes: v })} /><Field label="Access Key"><Input value={cfg.huaweiDns.accessKey} onChange={(e) => setDns({ accessKey: e.target.value })} /></Field><Field label="Secret Key"><Input type="password" value={cfg.huaweiDns.secretKey} onChange={(e) => setDns({ secretKey: e.target.value })} /></Field><div className="grid gap-4 md:col-span-2 md:grid-cols-2 xl:col-span-3 xl:grid-cols-3">{ISP_KEYS.map((key) => { const record = cfg.huaweiDns.records?.[key] ?? { zoneId: "", recordSetId: "", domain: "", ttl: 60 }; return <div key={key} className="space-y-3 rounded-xl border border-border bg-surface p-4"><div className="font-medium text-fg">{ispLabel(key)} DNS 记录</div><Field label="Zone ID"><Input value={record.zoneId} onChange={(e) => setRecord(key, { zoneId: e.target.value })} /></Field><Field label="RecordSet ID"><Input value={record.recordSetId} onChange={(e) => setRecord(key, { recordSetId: e.target.value })} /></Field><Field label="完整域名"><Input value={record.domain} onChange={(e) => setRecord(key, { domain: e.target.value })} /></Field><Num label="TTL（秒）" value={record.ttl} onChange={(v) => setRecord(key, { ttl: v })} /></div>; })}</div></CardBody></Card>;
}

function Num({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) { return <Field label={label}><Input type="number" value={value ?? 0} onChange={(e) => onChange(Number(e.target.value))} /></Field>; }
function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) { return <div className="flex items-center justify-between rounded-xl border border-border bg-surface p-3"><span className="text-sm text-fg-muted">{label}</span><Switch checked={checked} onChange={onChange} /></div>; }
