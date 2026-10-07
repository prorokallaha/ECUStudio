"use client";
import type { ReactNode } from "react";
import { cn } from "@/lib/cn";

export function Tabs<T extends string>({ value, onChange, items, className, right }: { value: T; onChange: (v: T) => void; items: { value: T; label: ReactNode; icon?: ReactNode; hidden?: boolean }[]; className?: string; right?: ReactNode }) {
  return (
    <div className={cn("flex items-center gap-0.5 border-b border-border px-2", className)}>
      {items.filter((i) => !i.hidden).map((i) => (
        <button
          key={i.value}
          onClick={() => onChange(i.value)}
          className={cn(
            "relative flex h-8 items-center gap-1.5 px-2.5 text-xs font-medium transition-colors",
            i.value === value ? "text-fg" : "text-fg-subtle hover:text-fg",
          )}
        >
          {i.icon}{i.label}
          {i.value === value && <span className="absolute inset-x-1 -bottom-px h-0.5 rounded bg-calc" />}
        </button>
      ))}
      {right && <div className="ml-auto flex items-center gap-1">{right}</div>}
    </div>
  );
}
