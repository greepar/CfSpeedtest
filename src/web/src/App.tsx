import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import type { ServerInfo, WebUiSessionOverview } from "@/lib/types";
import { ThemeProvider } from "@/lib/theme";
import { AppShell, type PageKey } from "@/components/AppShell";
import { LoginGate } from "@/components/LoginGate";
import { Button, Field, Input, Modal, ToastProvider, useToast } from "@/components/ui";
import { Overview } from "@/pages/Overview";
import { HistoryPage } from "@/pages/HistoryPage";
import { ClientsPage } from "@/pages/ClientsPage";
import { IpPoolPage } from "@/pages/IpPoolPage";
import { DnsPage } from "@/pages/DnsPage";
import { ConfigPage } from "@/pages/ConfigPage";

function Inner() {
  const toast = useToast();
  const [page, setPage] = useState<PageKey>("overview");
  const [passwordOpen, setPasswordOpen] = useState(false);
  const [version, setVersion] = useState("");

  useEffect(() => {
    api.get<ServerInfo>("/api/server/info", undefined, true)
      .then((info) => setVersion(info.version || ""))
      .catch(() => setVersion(""));
  }, []);

  return <LoginGate>{(auth, refreshAuth) => {
    async function logout() {
      await api.post<string>("/api/auth/logout");
      toast("已退出登录", "success");
      await refreshAuth();
    }
    return <AppShell page={page} setPage={setPage} username={auth.username} version={version} onLogout={logout} onPassword={() => setPasswordOpen(true)}>{page === "overview" && <Overview />}{page === "history" && <HistoryPage />}{page === "clients" && <ClientsPage />}{page === "ippool" && <IpPoolPage />}{page === "dns" && <DnsPage />}{page === "config" && <ConfigPage />}<PasswordModal open={passwordOpen} onClose={() => setPasswordOpen(false)} refreshAuth={refreshAuth} /></AppShell>;
  }}</LoginGate>;
}

function PasswordModal({ open, onClose, refreshAuth }: { open: boolean; onClose: () => void; refreshAuth: () => Promise<void> }) {
  const toast = useToast();
  const [currentPassword, setCurrentPassword] = useState("");
  const [newUsername, setNewUsername] = useState("admin");
  const [newPassword, setNewPassword] = useState("");
  const [sessions, setSessions] = useState<WebUiSessionOverview[]>([]);

  async function loadSessions() { setSessions(await api.get<WebUiSessionOverview[]>("/api/auth/sessions") ?? []); }
  async function save() {
    await api.post<string>("/api/auth/change-password", { currentPassword, newUsername, newPassword });
    toast("登录凭据已更新，请重新登录", "success");
    onClose();
    await refreshAuth();
  }
  return <Modal open={open} title="修改密码与会话" onClose={onClose} maxWidth="max-w-3xl" footer={<><Button variant="secondary" onClick={onClose}>取消</Button><Button onClick={save}>保存并重新登录</Button></>}><div className="space-y-5"><div className="grid gap-4 sm:grid-cols-3"><Field label="当前密码"><Input type="password" value={currentPassword} onChange={(e) => setCurrentPassword(e.target.value)} /></Field><Field label="新用户名"><Input value={newUsername} onChange={(e) => setNewUsername(e.target.value)} /></Field><Field label="新密码"><Input type="password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} /></Field></div><div><Button variant="secondary" onClick={loadSessions}>加载当前会话</Button><div className="mt-3 overflow-auto rounded-xl border border-border"><table className="w-full min-w-[620px] text-sm"><thead className="bg-surface text-left text-xs text-fg-subtle"><tr><th className="p-3">用户</th><th>IP</th><th>User-Agent</th><th>最后活动</th></tr></thead><tbody className="divide-y divide-border">{sessions.map((s, i) => <tr key={`${s.ipAddress}-${i}`}><td className="p-3">{s.username}</td><td>{s.ipAddress}</td><td className="max-w-sm truncate">{s.userAgent}</td><td>{new Date(s.lastSeenAtUtc).toLocaleString("zh-CN")}</td></tr>)}</tbody></table></div></div></div></Modal>;
}

export default function App() {
  return <ThemeProvider><ToastProvider><Inner /></ToastProvider></ThemeProvider>;
}
