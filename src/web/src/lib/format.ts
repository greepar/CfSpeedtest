export type ClassValue =
  | string
  | number
  | null
  | false
  | undefined
  | ClassValue[];

/** 轻量 className 合并（无外部依赖） */
export function cn(...inputs: ClassValue[]): string {
  const out: string[] = [];
  for (const x of inputs) {
    if (!x) continue;
    if (Array.isArray(x)) {
      const s = cn(...x);
      if (s) out.push(s);
    } else {
      out.push(String(x));
    }
  }
  return out.join(" ");
}

/** 速度：KB/s -> 自适应 KB/s / MB/s */
export function formatSpeed(kbps: number): string {
  if (!kbps || kbps <= 0) return "0 KB/s";
  if (kbps >= 1024) return `${(kbps / 1024).toFixed(2)} MB/s`;
  return `${kbps.toFixed(1)} KB/s`;
}

export function formatLatency(ms: number): string {
  if (!ms || ms <= 0) return "-";
  return `${ms.toFixed(1)} ms`;
}

export function formatLossRate(rate: number): string {
  return `${(rate * 100).toFixed(1)}%`;
}

export function formatNumber(n: number): string {
  return n.toLocaleString("zh-CN");
}

/** UTC 字符串 -> 本地时间显示 */
export function formatDateTime(iso?: string | null): string {
  if (!iso) return "-";
  const d = parseUtc(iso);
  if (!d || isNaN(d.getTime())) return "-";
  return d.toLocaleString("zh-CN", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

export function formatTime(iso?: string | null): string {
  if (!iso) return "-";
  const d = parseUtc(iso);
  if (!d || isNaN(d.getTime())) return "-";
  return d.toLocaleTimeString("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

/** 后端 DateTime 多为 UTC，但可能不带 Z，统一按 UTC 解析 */
export function parseUtc(iso: string): Date {
  if (/[zZ]$|[+-]\d{2}:?\d{2}$/.test(iso)) return new Date(iso);
  return new Date(iso + "Z");
}

/** 相对时间：例如 “3 分钟前” */
export function timeAgo(iso?: string | null): string {
  if (!iso) return "从未";
  const d = parseUtc(iso);
  if (isNaN(d.getTime())) return "-";
  const diff = Date.now() - d.getTime();
  if (diff < 0) return "刚刚";
  const sec = Math.floor(diff / 1000);
  if (sec < 60) return `${sec} 秒前`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min} 分钟前`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr} 小时前`;
  const day = Math.floor(hr / 24);
  if (day < 30) return `${day} 天前`;
  return formatDateTime(iso);
}

/** 倒计时：到目标时间剩余 mm:ss（已过为 00:00） */
export function countdownTo(targetIso?: string | null, nowMs?: number): string {
  if (!targetIso) return "--:--";
  const target = parseUtc(targetIso).getTime();
  const now = nowMs ?? Date.now();
  let s = Math.max(0, Math.floor((target - now) / 1000));
  const h = Math.floor(s / 3600);
  s -= h * 3600;
  const m = Math.floor(s / 60);
  s -= m * 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${pad(h)}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}
