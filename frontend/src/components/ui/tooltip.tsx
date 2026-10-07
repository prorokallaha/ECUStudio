"use client";
import { useRef, useState, type ReactNode } from "react";
import { cn } from "@/lib/cn";

/** Lightweight CSS tooltip (no portal) — adequate for dense desktop tables. */
export function Tooltip({ content, children, side = "top", className }: { content: ReactNode; children: ReactNode; side?: "top" | "bottom" | "right"; className?: string }) {
  const [open, setOpen] = useState(false);
  const [flip, setFlip] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);
  if (!content) return <>{children}</>;
  const show = () => { setFlip(side === "top" && (ref.current?.getBoundingClientRect().top ?? 999) < 90); setOpen(true); };
  if (flip && side === "top") side = "bottom";
  return (
    <span ref={ref} className="relative inline-flex" onMouseEnter={show} onMouseLeave={() => setOpen(false)} onFocus={show} onBlur={() => setOpen(false)}>
      {children}
      {open && (
        <span
          role="tooltip"
          className={cn(
            "pointer-events-none absolute z-50 w-max max-w-72 rounded-md border border-border-strong bg-bg-elev px-2 py-1.5 text-2xs leading-snug text-fg shadow-xl normal-case tracking-normal font-normal",
            side === "top" && "bottom-full left-1/2 mb-1.5 -translate-x-1/2",
            side === "bottom" && "top-full left-1/2 mt-1.5 -translate-x-1/2",
            side === "right" && "left-full top-1/2 ml-1.5 -translate-y-1/2",
            className,
          )}
        >
          {content}
        </span>
      )}
    </span>
  );
}
