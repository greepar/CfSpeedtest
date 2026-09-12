import { useEffect, useState } from "react";
import { Download, RefreshCw, Save } from "lucide-react";
import { api } from "@/lib/api";
import type { ServerConfig, ServerUpdateCheckResult } from "@/lib/types";
import {
  Button,
  Card,
  CardBody,
  CardHeader,
  Field,
  Input,
  Select,
  Switch,
  useToast,
} from "@/components/ui";

export function ConfigPage() {
  const toast = useToast();
  const [cfg, setCfg] = useState<ServerConfig | null>(null);
  const [saving, setSaving] = useState(false);
  const [checkingUpdate, setCheckingUpdate] = useState(false);
  const [updateCheck, setUpdateCheck] =
    useState<ServerUpdateCheckResult | null>(null);

  useEffect(() => {
    api
      .get<ServerConfig>("/api/config")
      .then((config) =>
        setCfg({
          ...config,
          clientUpdateRepository:
            config.clientUpdateRepository ||
            config.serverUpdateRepository ||
            "greepar/CfSpeedtest",
        }),
      )
      .catch(() => {});
  }, []);

  if (!cfg)
    return (
      <div className="py-8 text-center text-sm text-fg-muted">
        加载配置中...
      </div>
    );

  const set = <K extends keyof ServerConfig>(k: K, v: ServerConfig[K]) =>
    setCfg({ ...cfg, [k]: v });

  async function save() {
    setSaving(true);
    try {
      await api.post<string>("/api/config", cfg);
      toast("配置已保存", "success");
    } catch (error) {
      toast(error instanceof Error ? error.message : "配置保存失败", "error");
    } finally {
      setSaving(false);
    }
  }

  async function checkServerUpdate() {
    setCheckingUpdate(true);
    try {
      const result = await api.post<ServerUpdateCheckResult>(
        "/api/server/update/check",
      );
      setUpdateCheck(result);
      toast(result.message, result.updateAvailable ? "info" : "success");
    } catch (error) {
      setUpdateCheck(null);
      toast(error instanceof Error ? error.message : "检查更新失败", "error");
    } finally {
      setCheckingUpdate(false);
    }
  }

  async function installServerUpdate() {
    setCheckingUpdate(true);
    try {
      const message = await api.post<string>("/api/server/update/install");
      toast(message, "success");
    } catch (error) {
      toast(error instanceof Error ? error.message : "服务端更新失败", "error");
    } finally {
      setCheckingUpdate(false);
    }
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title="基础测速配置"
          desc="任务分发、测速参数和阈值"
          action={
            <Button loading={saving} onClick={save}>
              <Save className="h-4 w-4" />
              保存配置
            </Button>
          }
        />
        <CardBody className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          <Field label="测速 URL 模板">
            <Input
              value={cfg.testUrl}
              onChange={(e) => set("testUrl", e.target.value)}
            />
          </Field>
          <Field label="测速 Host">
            <Input
              value={cfg.testHost}
              onChange={(e) => set("testHost", e.target.value)}
            />
          </Field>
          <Num
            label="测速端口"
            value={cfg.testPort}
            onChange={(v) => set("testPort", v)}
          />
          <Num
            label="下载测速时长（秒）"
            value={cfg.downloadDurationSeconds}
            onChange={(v) => set("downloadDurationSeconds", v)}
          />
          <Num
            label="TCP 测试时长（秒）"
            value={cfg.tcpTestDurationSeconds}
            onChange={(v) => set("tcpTestDurationSeconds", v)}
          />
          <Num
            label="每批 IP 数"
            value={cfg.batchSize}
            onChange={(v) => set("batchSize", v)}
          />
          <Num
            label="返回 TopN"
            value={cfg.topN}
            onChange={(v) => set("topN", v)}
          />
          <Num
            label="单轮最多测试 IP"
            value={cfg.maxTestIpCount}
            onChange={(v) => set("maxTestIpCount", v)}
          />
          <Num
            label="客户端间隔（分钟）"
            value={cfg.clientIntervalMinutes}
            onChange={(v) => set("clientIntervalMinutes", v)}
          />
          <Num
            label="心跳间隔（秒）"
            value={cfg.heartbeatIntervalSeconds}
            onChange={(v) => set("heartbeatIntervalSeconds", v)}
          />
          <Num
            label="IP 源自动拉取间隔（分钟）"
            value={cfg.apiRefreshIntervalMinutes}
            onChange={(v) => set("apiRefreshIntervalMinutes", v)}
          />
          <Num
            label="最低下载速度 KB/s"
            value={cfg.minDownloadSpeedKBps}
            onChange={(v) => set("minDownloadSpeedKBps", v)}
          />
          <Num
            label="下载限速 KB/s（0不限）"
            value={cfg.maxDownloadSpeedKBps}
            onChange={(v) => set("maxDownloadSpeedKBps", v)}
          />
        </CardBody>
      </Card>

      <Card>
        <CardHeader
          title="交叉测速策略"
          desc="先做多节点可用性硬门槛，再用中位数聚合通过节点的测速指标"
        />
        <CardBody className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          <Toggle
            label="启用同运营商交叉测速"
            checked={!!cfg.crossTestEnabled}
            onChange={(v) => set("crossTestEnabled", v)}
          />
          <Num
            label="候选 IP 数量"
            value={cfg.crossTestCandidateCount ?? 10}
            onChange={(v) => set("crossTestCandidateCount", v)}
          />
          <Field
            label="通过策略"
            hint="全部节点最安全；按比例适合节点较多且允许个别节点异常"
          >
            <Select
              value={cfg.crossTestPassPolicy ?? "all"}
              onChange={(e) =>
                set("crossTestPassPolicy", e.target.value as "all" | "ratio")
              }
            >
              <option value="all">全部节点通过</option>
              <option value="ratio">按通过比例</option>
            </Select>
          </Field>
          <Num
            label="最低通过比例（%）"
            value={cfg.crossTestMinPassRatePercent ?? 80}
            onChange={(v) => set("crossTestMinPassRatePercent", v)}
          />
          <Num
            label="最大允许丢包率（%）"
            value={cfg.crossTestMaxPacketLossPercent ?? 20}
            onChange={(v) => set("crossTestMaxPacketLossPercent", v)}
          />
          <Num
            label="最低有效节点数"
            value={cfg.crossTestMinValidReports ?? 2}
            onChange={(v) => set("crossTestMinValidReports", v)}
          />
        </CardBody>
      </Card>

      <Card>
        <CardHeader
          title="服务端自动更新"
          desc="原生部署自动安装 GitHub Release；Docker 部署请更新镜像"
          action={
            <Button
              variant={updateCheck?.updateAvailable ? "primary" : "secondary"}
              loading={checkingUpdate}
              onClick={
                updateCheck?.updateAvailable
                  ? installServerUpdate
                  : checkServerUpdate
              }
            >
              {updateCheck?.updateAvailable ? (
                <Download className="h-4 w-4" />
              ) : (
                <RefreshCw className="h-4 w-4" />
              )}
              {updateCheck?.updateAvailable ? "立即更新" : "检查更新"}
            </Button>
          }
        />
        <CardBody className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          <Toggle
            label="启用服务端自动更新"
            checked={!!cfg.serverAutoUpdateEnabled}
            onChange={(v) => set("serverAutoUpdateEnabled", v)}
          />
          <Num
            label="检查间隔（分钟）"
            value={cfg.serverUpdateIntervalMinutes ?? 360}
            onChange={(v) => set("serverUpdateIntervalMinutes", v)}
          />
          <Field label="GitHub 仓库">
            <Input
              value={cfg.serverUpdateRepository ?? ""}
              onChange={(e) => set("serverUpdateRepository", e.target.value)}
            />
          </Field>
          <Field label="GH Proxy 前缀">
            <Input
              value={cfg.serverUpdateGhProxyPrefix ?? ""}
              onChange={(e) => set("serverUpdateGhProxyPrefix", e.target.value)}
            />
          </Field>
        </CardBody>
      </Card>

      <Card>
        <CardHeader title="客户端与更新" desc="白名单、代理和自动更新源" />
        <CardBody className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          <Toggle
            label="只允许白名单客户端"
            checked={cfg.clientWhitelistOnly}
            onChange={(v) => set("clientWhitelistOnly", v)}
          />
          <Toggle
            label="手动 IP 优先"
            checked={cfg.manualIpPriorityEnabled}
            onChange={(v) => set("manualIpPriorityEnabled", v)}
          />
          <Toggle
            label="测速后自动清理 IP 池"
            checked={cfg.autoCleanupEnabled}
            onChange={(v) => set("autoCleanupEnabled", v)}
          />
          <Toggle
            label="启用客户端更新"
            checked={cfg.clientUpdateEnabled}
            onChange={(v) => set("clientUpdateEnabled", v)}
          />
          <Field label="代理模式">
            <Select
              value={cfg.clientProxyMode}
              onChange={(e) => set("clientProxyMode", e.target.value)}
            >
              <option value="direct">direct</option>
              <option value="system">system</option>
              <option value="custom">custom</option>
            </Select>
          </Field>
          <Field label="自定义代理">
            <Input
              value={cfg.clientProxyUrl}
              onChange={(e) => set("clientProxyUrl", e.target.value)}
            />
          </Field>
          <Field label="GitHub 仓库">
            <Input
              value={cfg.clientUpdateRepository}
              onChange={(e) => set("clientUpdateRepository", e.target.value)}
            />
          </Field>
          <Field label="GH Proxy 前缀">
            <Input
              value={cfg.clientUpdateGhProxyPrefix}
              onChange={(e) => set("clientUpdateGhProxyPrefix", e.target.value)}
            />
          </Field>
        </CardBody>
      </Card>

      <Card>
        <CardHeader
          title="WebUI 安全"
          desc="用户名和密码请使用页面顶部的“修改密码”功能管理"
        />
        <CardBody className="grid gap-4 md:grid-cols-3">
          <Toggle
            label="启用 WebUI 登录保护"
            checked={cfg.webUiAuth.enabled}
            onChange={(v) => set("webUiAuth", { ...cfg.webUiAuth, enabled: v })}
          />
          <Field
            label="允许连续失败次数"
            hint="同一来源 IP 达到该次数后临时禁止登录"
          >
            <Input
              type="number"
              min="1"
              max="100"
              value={cfg.webUiAuth.maxFailedLoginAttempts ?? 5}
              onChange={(e) =>
                set("webUiAuth", {
                  ...cfg.webUiAuth,
                  maxFailedLoginAttempts: Number(e.target.value),
                })
              }
            />
          </Field>
          <Field label="限流时间（分钟）" hint="限流到期后允许重新尝试登录">
            <Input
              type="number"
              min="1"
              max="1440"
              value={cfg.webUiAuth.loginLockoutMinutes ?? 15}
              onChange={(e) =>
                set("webUiAuth", {
                  ...cfg.webUiAuth,
                  loginLockoutMinutes: Number(e.target.value),
                })
              }
            />
          </Field>
        </CardBody>
      </Card>
    </div>
  );
}

function Num({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number;
  onChange: (v: number) => void;
}) {
  return (
    <Field label={label}>
      <Input
        type="number"
        value={value ?? 0}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </Field>
  );
}

function Toggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <div className="flex items-center justify-between rounded-xl border border-border bg-surface p-3">
      <span className="text-sm text-fg-muted">{label}</span>
      <Switch checked={checked} onChange={onChange} />
    </div>
  );
}
