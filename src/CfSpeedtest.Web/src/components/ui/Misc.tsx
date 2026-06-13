import { Loader2 } from "lucide-react";
import { cn } from "@/lib/format";

export function Spinner({ label = "加载中..." }: { label?: string }) {
  return <div className="flex items-center justify-center gap-2 py-10 text-sm text-fg-muted"><Loader2 className="h-4 w-4 animate-spin" />{label}</div>;
}

export function Empty({ title = "暂无数据", desc }: { title?: string; desc?: string }) {
  return <div className="rounded-xl border border-dashed border-border bg-surface/60 p-8 text-center"><div className="text-sm font-medium text-fg">{title}</div>{desc && <div className="mt-1 text-xs text-fg-muted">{desc}</div>}</div>;
}

export function Progress({ value, className }: { value: number; className?: string }) {
  const v = Math.max(0, Math.min(100, value));
  return <div className={cn("h-2 overflow-hidden rounded-full bg-surface-2", className)}><div className="h-full rounded-full bg-primary transition-all" style={{ width: `${v}%` }} /></div>;
}

export function Segmented<T extends string>({ value, options, onChange }: { value: T; options: { label: string; value: T }[]; onChange: (v: T) => void }) {
  return <div className="inline-flex rounded-lg border border-border bg-surface p-1">{options.map((o) => <button key={o.value} type="button" onClick={() => onChange(o.value)} className={cn("rounded-md px-3 py-1.5 text-sm transition", value === o.value ? "bg-card text-fg shadow-sm" : "text-fg-muted hover:text-fg")}>{o.label}</button>)}</div>;
}

export function CodeBox({ value }: { value: string }) {
  return <pre className="overflow-auto rounded-xl border border-border bg-surface p-3 font-mono text-xs text-fg">{value}</pre>;
}
