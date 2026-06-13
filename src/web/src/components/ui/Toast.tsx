import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";
import { CheckCircle2, AlertCircle, Info, X } from "lucide-react";
import { Button } from "./Button";

type ToastKind = "success" | "error" | "info";
interface ToastItem { id: number; kind: ToastKind; message: string }
const Ctx = createContext<{ toast: (message: string, kind?: ToastKind) => void } | null>(null);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const toast = useCallback((message: string, kind: ToastKind = "info") => {
    const id = Date.now() + Math.random();
    setItems((x) => [...x, { id, kind, message }]);
    window.setTimeout(() => setItems((x) => x.filter((t) => t.id !== id)), 3500);
  }, []);
  const value = useMemo(() => ({ toast }), [toast]);
  return (
    <Ctx.Provider value={value}>
      {children}
      <div className="fixed right-4 top-4 z-[60] grid w-[min(420px,calc(100vw-2rem))] gap-2">
        {items.map((t) => (
          <div key={t.id} className="flex animate-slide-in items-start gap-3 rounded-xl border border-border bg-card p-3 shadow-pop">
            {t.kind === "success" ? <CheckCircle2 className="mt-0.5 h-5 w-5 text-success" /> : t.kind === "error" ? <AlertCircle className="mt-0.5 h-5 w-5 text-danger" /> : <Info className="mt-0.5 h-5 w-5 text-info" />}
            <div className="min-w-0 flex-1 text-sm text-fg">{t.message}</div>
            <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => setItems((x) => x.filter((i) => i.id !== t.id))}><X className="h-4 w-4" /></Button>
          </div>
        ))}
      </div>
    </Ctx.Provider>
  );
}

export function useToast() {
  const c = useContext(Ctx);
  if (!c) throw new Error("useToast must be used within ToastProvider");
  return c.toast;
}
