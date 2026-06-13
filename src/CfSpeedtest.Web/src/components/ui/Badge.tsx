import { cn } from "@/lib/format";

export type BadgeTone = "default" | "primary" | "success" | "warning" | "danger" | "info";
const tones: Record<BadgeTone, string> = {
  default: "border-border bg-surface-2 text-fg-muted",
  primary: "border-primary/25 bg-primary-soft text-primary",
  success: "border-success/25 bg-success-soft text-success",
  warning: "border-warning/25 bg-warning-soft text-warning",
  danger: "border-danger/25 bg-danger-soft text-danger",
  info: "border-info/25 bg-info-soft text-info",
};

export function Badge({ children, tone = "default", className }: { children: React.ReactNode; tone?: BadgeTone; className?: string }) {
  return <span className={cn("inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium", tones[tone], className)}>{children}</span>;
}
