let serverTimeOffsetMs: number | null = null;

export function syncServerClock(dateHeader: string | null): void {
  if (!dateHeader) return;
  const serverTime = Date.parse(dateHeader);
  if (!Number.isFinite(serverTime)) return;
  serverTimeOffsetMs = serverTime - Date.now();
}

export function serverNow(): number {
  return Date.now() + (serverTimeOffsetMs ?? 0);
}
