"use client";
import { cn } from "@/lib/cn";
import { toneBg, type Tone } from "@/lib/colors";
import { useT } from "@/i18n";

/**
 * Load bar. When the value is unreliable (`exact=false`) it renders a coarse, hatched band
 * instead of a precise percentage — the spec forbids exact % on unreliable data.
 */
export function LoadBar({ value, low, high, tone, exact = true, className }: { value?: number | null; low?: number | null; high?: number | null; tone: Tone; exact?: boolean; className?: string }) {
  const t = useT();
  const v = Math.max(0, Math.min(1.2, value ?? 0));
  const lo = Math.max(0, Math.min(1.2, low ?? v));
  const hi = Math.max(0, Math.min(1.2, high ?? v));
  const W = (x: number) => `${(x / 1.2) * 100}%`;
  return (
    <div className={cn("relative h-2 w-full overflow-hidden rounded-full bg-panel-2", className)}>
      {exact ? (
        <>
          <div className={cn("absolute inset-y-0 left-0 rounded-full opacity-35", toneBg[tone])} style={{ width: W(hi) }} />
          <div className={cn("absolute inset-y-0 left-0 rounded-full", toneBg[tone])} style={{ width: W(v) }} />
          <div className="absolute inset-y-0 w-px bg-fg/50" style={{ left: W(lo) }} />
        </>
      ) : (
        <div
          className={cn("absolute inset-y-0 left-0 rounded-full opacity-60", toneBg[tone])}
          style={{ width: W(hi || v), backgroundImage: "repeating-linear-gradient(135deg, transparent 0 4px, rgba(0,0,0,.35) 4px 8px)" }}
        />
      )}
      <div className="absolute inset-y-0 w-px bg-fg/70" style={{ left: W(1) }} title={t("estimate.knownLimit")} />
    </div>
  );
}

export function StepBar({ fraction, className }: { fraction: number; className?: string }) {
  return (
    <div className={cn("h-1 w-full overflow-hidden rounded-full bg-panel-2", className)}>
      <div className="h-full bg-calc transition-all" style={{ width: `${Math.round(Math.max(0, Math.min(1, fraction)) * 100)}%` }} />
    </div>
  );
}
