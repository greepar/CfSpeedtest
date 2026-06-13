import { X } from "lucide-react";
import { Button } from "./Button";

export function Modal({ open, title, children, footer, onClose, maxWidth = "max-w-2xl" }: { open: boolean; title: string; children: React.ReactNode; footer?: React.ReactNode; onClose: () => void; maxWidth?: string }) {
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-4 backdrop-blur-sm">
      <div className={`max-h-[90vh] w-full ${maxWidth} animate-scale-in overflow-hidden rounded-2xl border border-border bg-card shadow-pop`}>
        <div className="flex items-center justify-between border-b border-border px-5 py-4">
          <h3 className="font-semibold text-fg">{title}</h3>
          <Button variant="ghost" size="icon" onClick={onClose}><X className="h-4 w-4" /></Button>
        </div>
        <div className="max-h-[70vh] overflow-auto p-5">{children}</div>
        {footer && <div className="flex justify-end gap-2 border-t border-border px-5 py-4">{footer}</div>}
      </div>
    </div>
  );
}
