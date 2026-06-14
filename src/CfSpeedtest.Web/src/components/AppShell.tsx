import { Activity, Cloud, Database, FileClock, Globe2, LayoutDashboard, LogOut, Menu, Moon, Settings, Sun, Users, X } from "lucide-react";
import { useState } from "react";
import type { ComponentType, ReactNode } from "react";
import { Button } from "./ui";
import { cn } from "@/lib/format";
import { useTheme } from "@/lib/theme";

export type PageKey = "overview" | "history" | "clients" | "ippool" | "dns" | "config";

const nav: { key: PageKey; label: string; icon: ComponentType<{ className?: string }> }[] = [
  { key: "overview", label: "概览", icon: LayoutDashboard },
  { key: "history", label: "测速记录", icon: FileClock },
  { key: "clients", label: "客户端", icon: Users },
  { key: "ippool", label: "IP 池", icon: Database },
  { key: "dns", label: "DNS 更新", icon: Globe2 },
  { key: "config", label: "配置", icon: Settings },
];

export function AppShell({ page, setPage, username, version, onLogout, onPassword, children }: { page: PageKey; setPage: (p: PageKey) => void; username: string; version: string; onLogout: () => void; onPassword: () => void; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const { resolved, toggle } = useTheme();
  const title = nav.find((n) => n.key === page)?.label ?? "概览";
  const Sidebar = (
    <aside className="flex h-full w-72 flex-col border-r border-border bg-card/85 backdrop-blur-xl">
      <div className="flex h-16 items-center gap-3 border-b border-border px-5">
        <div className="grid h-10 w-10 place-items-center rounded-xl bg-primary-soft text-primary"><Cloud className="h-5 w-5" /></div>
        <div>
          <div className="font-semibold text-fg">CfSpeedtest</div>
          <div className="text-xs text-fg-subtle">Cloudflare IP 管理面板</div>
        </div>
      </div>
      <nav className="flex-1 space-y-1 p-3">
        {nav.map((n) => {
          const Icon = n.icon;
          const active = page === n.key;
          return <button key={n.key} onClick={() => { setPage(n.key); setOpen(false); }} className={cn("flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-sm transition", active ? "bg-primary-soft text-primary" : "text-fg-muted hover:bg-card-hover hover:text-fg")}><Icon className="h-4 w-4" />{n.label}</button>;
        })}
      </nav>
      <div className="border-t border-border p-4 text-xs text-fg-subtle truncate">CfSpeedtest {version ? `v${version.split("+")[0]}` : ""}</div>
    </aside>
  );
  return (
    <div className="min-h-full bg-surface text-fg">
      <div className="fixed inset-y-0 left-0 z-30 hidden lg:block">{Sidebar}</div>
      {open && <div className="fixed inset-0 z-40 lg:hidden"><div className="absolute inset-0 bg-black/45" onClick={() => setOpen(false)} />{Sidebar}<Button variant="secondary" size="icon" className="absolute right-4 top-4" onClick={() => setOpen(false)}><X className="h-4 w-4" /></Button></div>}
      <div className="lg:pl-72">
        <header className="sticky top-0 z-20 flex h-16 items-center justify-between border-b border-border bg-surface/80 px-4 backdrop-blur-xl sm:px-6">
          <div className="flex items-center gap-3">
            <Button variant="ghost" size="icon" className="lg:hidden" onClick={() => setOpen(true)}><Menu className="h-5 w-5" /></Button>
            <div>
              <h1 className="text-lg font-semibold text-fg">{title}</h1>
              <div className="hidden items-center gap-1 text-xs text-fg-subtle sm:flex"><Activity className="h-3 w-3 text-success" /> 服务运行中</div>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Button variant="ghost" size="icon" onClick={toggle}>{resolved === "dark" ? <Moon className="h-4 w-4" /> : <Sun className="h-4 w-4" />}</Button>
            <Button variant="secondary" className="hidden sm:inline-flex" onClick={onPassword}>修改密码</Button>
            <span className="hidden text-sm text-fg-muted sm:inline">{username || "admin"}</span>
            <Button variant="ghost" size="icon" onClick={onLogout}><LogOut className="h-4 w-4" /></Button>
          </div>
        </header>
        <main className="mx-auto max-w-7xl p-4 sm:p-6 lg:p-8">{children}</main>
      </div>
    </div>
  );
}
