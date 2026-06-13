import { useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { Lock, ShieldCheck } from "lucide-react";
import { api, setUnauthorizedHandler } from "@/lib/api";
import type { WebUiAuthStatus } from "@/lib/types";
import { Button, Field, Input, Modal, useToast } from "./ui";

export function LoginGate({ children }: { children: (auth: WebUiAuthStatus, refresh: () => Promise<void>) => ReactNode }) {
  const toast = useToast();
  const [auth, setAuth] = useState<WebUiAuthStatus | null>(null);
  const [loginOpen, setLoginOpen] = useState(false);
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [loading, setLoading] = useState(false);

  const refresh = async () => {
    const s = await api.get<WebUiAuthStatus>("/api/auth/status", undefined, true);
    setAuth(s);
    setLoginOpen(s.enabled && !s.authenticated);
    if (s.username) setUsername(s.username);
  };

  useEffect(() => {
    setUnauthorizedHandler(() => setLoginOpen(true));
    refresh().catch(() => setLoginOpen(true));
  }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setLoading(true);
    try {
      const s = await api.post<WebUiAuthStatus>("/api/auth/login", { username, password });
      setAuth(s);
      setLoginOpen(false);
      setPassword("");
      toast("登录成功", "success");
    } catch (err) {
      toast(err instanceof Error ? err.message : "登录失败", "error");
    } finally {
      setLoading(false);
    }
  }

  if (!auth) return <div className="grid min-h-screen place-items-center bg-surface text-fg-muted">正在检查登录状态...</div>;
  return (
    <>
      {auth.authenticated || !auth.enabled ? children(auth, refresh) : <div className="min-h-screen bg-surface" />}
      <Modal open={loginOpen} title="登录管理面板" onClose={() => {}} maxWidth="max-w-md">
        <form onSubmit={submit} className="space-y-5">
          <div className="flex items-center gap-3 rounded-xl border border-primary/20 bg-primary-soft p-4 text-primary"><ShieldCheck className="h-5 w-5" /><div className="text-sm">请输入管理员凭据以继续访问。</div></div>
          <Field label="用户名"><Input autoFocus value={username} onChange={(e) => setUsername(e.target.value)} /></Field>
          <Field label="密码"><Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
          <Button className="w-full" loading={loading}><Lock className="h-4 w-4" />登录</Button>
        </form>
      </Modal>
    </>
  );
}
