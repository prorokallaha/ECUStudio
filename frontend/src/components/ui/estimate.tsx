"use client";
import type { Estimate } from "@/types/domain";
import { fmtEstimate } from "@/lib/format";
import { cn } from "@/lib/cn";
import { Tooltip } from "./tooltip";

/**
 * Engineering value with range. Never renders a calculated number as a bare measurement:
 * the range is always visible (or available in the tooltip for compact mode).
 */
export function EstimateValue({ e, size = "md", compact = false, className }: { e?: Estimate | null; size?: "sm" | "md" | "lg" | "xl"; compact?: boolean; className?: string }) {
  const t = fmtEstimate(e);
  const big = { sm: "text-xs", md: "text-sm", lg: "text-xl", xl: "text-3xl" }[size];
  if (t.state !== "known")
    return (
      <Tooltip content={e?.note ?? (t.state === "na" ? "Not applicable for this hardware" : "Not enough data to estimate")}>
        <span className={cn("font-semibold uppercase tracking-wide text-unknown", size === "xl" ? "text-lg" : "text-xs", className)}>{t.value}</span>
      </Tooltip>
    );
  const tip = `${e?.kind ?? "Estimated"} · confidence ${(e?.confidence ?? 0).toFixed(2)}${e?.note ? ` · ${e.note}` : ""}`;
  return (
    <Tooltip content={tip}>
      <span className={cn("inline-flex items-baseline gap-1 whitespace-nowrap", className)}>
        <span className={cn("num font-semibold", big)}>{t.value}</span>
        {t.unit && <span className="text-2xs text-fg-subtle">{t.unit}</span>}
        {t.range && !compact && <span className="num text-2xs text-fg-subtle">({t.range})</span>}
      </span>
    </Tooltip>
  );
}
