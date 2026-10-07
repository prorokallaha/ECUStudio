"use client";
import { useEffect, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { X } from "lucide-react";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

export function Dialog({ open, onClose, title, children, footer, className }: { open: boolean; onClose: () => void; title: ReactNode; children: ReactNode; footer?: ReactNode; className?: string }) {
  const t = useT();
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);
  if (!open || typeof document === "undefined") return null;
  return createPortal(
    <div className="fixed inset-0 z-[100] flex items-start justify-center bg-black/60 pt-[12vh]" onMouseDown={onClose}>
      <div className={cn("w-full max-w-lg rounded-lg border border-border-strong bg-bg-elev shadow-2xl", className)} onMouseDown={(e) => e.stopPropagation()} role="dialog" aria-modal>
        <div className="flex items-center justify-between border-b border-border px-4 h-10">
          <div className="text-sm font-semibold">{title}</div>
          <button onClick={onClose} className="text-fg-subtle hover:text-fg" aria-label={t("common.close")}><X className="size-4" /></button>
        </div>
        <div className="p-4">{children}</div>
        {footer && <div className="flex justify-end gap-2 border-t border-border px-4 py-2.5">{footer}</div>}
      </div>
    </div>,
    document.body,
  );
}
