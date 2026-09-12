import { useEffect, useState } from "react";
import { BellRing, FlaskConical, Minus, Plus, Save } from "lucide-react";
import { api } from "@/lib/api";
import type { WebhookConfig } from "@/lib/types";
import {
  Button,
  Card,
  CardBody,
  CardHeader,
  Field,
  Input,
  Switch,
  Textarea,
  useToast,
} from "@/components/ui";

const DEFAULT_BODY_TEMPLATE = `{
  "text": "{{message}}\\n事件：{{eventType}}\\n发生时间：{{occurredAtUtc}}\\n客户端 ID：{{clientId}}\\n客户端名称：{{clientName}}\\n运营商编号：{{isp}}\\n运营商：{{ispName}}\\n在线状态：{{online}}\\n最后心跳：{{lastSeenAtUtc}}\\n客户端版本：{{version}}\\n客户端平台：{{platform}}"
}`;

export function NotificationsPage() {
  const toast = useToast();
  const [config, setConfig] = useState<WebhookConfig | null>(null);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);

  useEffect(() => {
    api
      .get<WebhookConfig>("/api/notifications/config")
      .then(setConfig)
      .catch(() => {});
  }, []);

  if (!config)
    return (
      <div className="py-8 text-center text-sm text-fg-muted">
        加载通知配置中...
      </div>
    );

  async function save() {
    setSaving(true);
    try {
      const message = await api.post<string>(
        "/api/notifications/config",
        config,
      );
      toast(message, "success");
    } catch (error) {
      toast(
        error instanceof Error ? error.message : "Webhook 配置保存失败",
        "error",
      );
    } finally {
      setSaving(false);
    }
  }

  async function test() {
    setTesting(true);
    try {
      await api.post<string>("/api/notifications/config", config);
      const message = await api.post<string>("/api/notifications/test");
      toast(message, "success");
    } catch (error) {
      toast(
        error instanceof Error ? error.message : "Webhook 测试失败",
        "error",
      );
    } finally {
      setTesting(false);
    }
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader
          title={
            <span className="flex items-center gap-2">
              <BellRing className="h-5 w-5 text-primary" />
              Webhook 通知
            </span>
          }
          desc="客户端上线或超过心跳超时时间离线时，向指定地址发送 JSON 通知"
          action={
            <>
              <Button variant="secondary" loading={testing} onClick={test}>
                <FlaskConical className="h-4 w-4" />
                测试 Webhook
              </Button>
              <Button loading={saving} onClick={save}>
                <Save className="h-4 w-4" />
                保存配置
              </Button>
            </>
          }
        />
        <CardBody className="space-y-5">
          <Toggle
            label="启用 Webhook 通知"
            checked={config.enabled}
            onChange={(enabled) => setConfig({ ...config, enabled })}
          />
          <Field
            label="Webhook URL"
            hint="仅支持 HTTP/HTTPS；请求方法固定为 POST，Content-Type 为 application/json"
          >
            <Input
              type="url"
              value={config.url}
              onChange={(e) => setConfig({ ...config, url: e.target.value })}
              placeholder="https://example.com/webhook"
            />
          </Field>
          <div className="space-y-2">
            <div className="flex items-end justify-between gap-2">
              <div>
                <div className="text-xs font-medium text-fg-muted">
                  自定义请求头（可选）
                </div>
                <div className="mt-1 text-xs text-fg-subtle">
                  可配置 Authorization、签名或接收端要求的其他请求头。
                </div>
              </div>
              <Button
                variant="secondary"
                size="sm"
                onClick={() =>
                  setConfig({
                    ...config,
                    headers: [...config.headers, { name: "", value: "" }],
                  })
                }
              >
                <Plus className="h-4 w-4" />
                添加
              </Button>
            </div>
            {!config.headers.length ? (
              <div className="rounded-xl border border-dashed border-border p-4 text-center text-sm text-fg-subtle">
                暂无自定义请求头
              </div>
            ) : (
              <div className="space-y-2">
                {config.headers.map((header, index) => (
                  <div
                    key={index}
                    className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,2fr)_auto]"
                  >
                    <Input
                      value={header.name}
                      onChange={(e) =>
                        setConfig({
                          ...config,
                          headers: config.headers.map((item, i) =>
                            i === index
                              ? { ...item, name: e.target.value }
                              : item,
                          ),
                        })
                      }
                      placeholder="请求头名称，如 Authorization"
                    />
                    <Input
                      value={header.value}
                      onChange={(e) =>
                        setConfig({
                          ...config,
                          headers: config.headers.map((item, i) =>
                            i === index
                              ? { ...item, value: e.target.value }
                              : item,
                          ),
                        })
                      }
                      placeholder="请求头值，如 Bearer token"
                    />
                    <Button
                      variant="secondary"
                      size="icon"
                      title="删除此请求头"
                      onClick={() =>
                        setConfig({
                          ...config,
                          headers: config.headers.filter((_, i) => i !== index),
                        })
                      }
                    >
                      <Minus className="h-4 w-4" />
                    </Button>
                  </div>
                ))}
              </div>
            )}
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            <Toggle
              label="客户端上线通知"
              checked={config.notifyClientOnline}
              onChange={(notifyClientOnline) =>
                setConfig({ ...config, notifyClientOnline })
              }
            />
            <Toggle
              label="客户端离线通知"
              checked={config.notifyClientOffline}
              onChange={(notifyClientOffline) =>
                setConfig({ ...config, notifyClientOffline })
              }
            />
          </div>
          <Field
            label="离线通知延迟（秒）"
            hint="检测到客户端离线后继续等待；等待期间恢复连接则取消通知。设置为 0 表示检测到离线后立即通知。"
          >
            <Input
              type="number"
              min="0"
              max="86400"
              value={config.offlineNotificationDelaySeconds ?? 60}
              onChange={(e) =>
                setConfig({
                  ...config,
                  offlineNotificationDelaySeconds: Number(e.target.value),
                })
              }
            />
          </Field>
          <div className="space-y-2">
            <div className="flex flex-wrap items-end justify-between gap-2">
              <div>
                <div className="text-xs font-medium text-fg-muted">
                  JSON 请求体模板
                </div>
                <div className="mt-1 text-xs text-fg-subtle">
                  默认模板包含全部可用占位符；字符串占位符需要写在双引号内，isp
                  和 online 不需要双引号。
                </div>
              </div>
              <Button
                variant="secondary"
                size="sm"
                onClick={() =>
                  setConfig({ ...config, bodyTemplate: DEFAULT_BODY_TEMPLATE })
                }
              >
                恢复默认模板
              </Button>
            </div>
            <Textarea
              className="min-h-[220px] font-mono text-xs leading-6"
              value={config.bodyTemplate}
              onChange={(e) =>
                setConfig({ ...config, bodyTemplate: e.target.value })
              }
              spellCheck={false}
            />
            <div className="text-xs text-fg-subtle">
              保存和测试时，服务端会先替换占位符并校验最终 JSON。
            </div>
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHeader
          title="可用占位符"
          desc="接收端返回任意 2xx 状态码即视为发送成功"
        />
        <CardBody className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {[
            ["{{eventType}}", "client.online / client.offline"],
            ["{{occurredAtUtc}}", "事件 UTC 时间"],
            ["{{clientId}}", "客户端 ID"],
            ["{{clientName}}", "客户端名称"],
            ["{{isp}}", "运营商数字值"],
            ["{{ispName}}", "运营商名称"],
            ["{{online}}", "true / false"],
            ["{{lastSeenAtUtc}}", "最后心跳 UTC 时间"],
            ["{{version}}", "客户端版本"],
            ["{{platform}}", "客户端平台"],
            ["{{message}}", "中文事件消息"],
          ].map(([placeholder, description]) => (
            <div
              key={placeholder}
              className="rounded-xl border border-border bg-surface p-3"
            >
              <code className="text-xs font-medium text-primary">
                {placeholder}
              </code>
              <div className="mt-1 text-xs text-fg-muted">{description}</div>
            </div>
          ))}
        </CardBody>
      </Card>
    </div>
  );
}

function Toggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <div className="flex items-center justify-between rounded-xl border border-border bg-surface p-3">
      <span className="text-sm text-fg-muted">{label}</span>
      <Switch checked={checked} onChange={onChange} />
    </div>
  );
}
