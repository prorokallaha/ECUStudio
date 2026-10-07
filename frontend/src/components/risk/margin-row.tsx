"use client";
import type { ComponentMargin } from "@/types/domain";
import { Badge, ConfidenceBadge, LoadBar, SeverityBadge, EstimateValue } from "@/components/ui";
import { severityTone } from "@/lib/colors";
import { fmtParam } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

const LEVEL_FRACTION: Record<string, number> = { Unknown: 0, Low: 0.35, Moderate: 0.6, High: 0.85, VeryHigh: 1.05 };

/** Component margin: exact utilization only when limit and load are reliable; otherwise a coarse load-level band. */
export function MarginRow({ c, active, onClick }: { c: ComponentMargin; active?: boolean; onClick?: () => void }) {
  const t = useT();
  const tone = severityTone[c.severity];
  const na = c.load?.kind === "NotApplicable";
  const exact = !na && c.showExactUtilization && c.utilization?.value !== undefined;
  return (
    <button onClick={onClick} className={cn("grid w-full grid-cols-[minmax(140px,190px)_minmax(160px,1fr)_130px_84px_96px] items-center gap-3 border-b border-border px-3 py-2 text-left text-xs hover:bg-panel-2", active && "bg-calc/10")}>
      <div className="min-w-0">
        <div className="truncate font-medium">{c.label}</div>
        <div className="truncate text-[10px] text-fg-subtle">{c.metric}</div>
      </div>
      <div className="space-y-1">
        <LoadBar tone={tone} exact={exact} value={exact ? c.utilization!.value! / 100 : LEVEL_FRACTION[c.loadLevel] ?? 0} low={exact ? (c.utilization!.low ?? 0) / 100 : undefined} high={exact ? (c.utilization!.high ?? 0) / 100 : undefined} />
        <div className="flex justify-between gap-2 whitespace-nowrap text-[10px] text-fg-subtle">
          <span className="truncate" title={exact ? undefined : t("risks.tooUncertain")}>{na ? t("risks.notApplicable") : exact ? t("risks.ofLimit", { v: c.utilization!.value!.toFixed(0), lo: c.utilization!.low?.toFixed(0), hi: c.utilization!.high?.toFixed(0) }) : `${t("risks.load", { level: t.tx(`loadLevel.${c.loadLevel}`, c.loadLevel).toUpperCase() })}${c.severity === "Unknown" ? t("risks.limitUnknown") : ""}`}</span>
          {c.changeVsStockPct !== undefined && c.changeVsStockPct !== null && <span className="num">{t("risks.vsStock", { v: `${c.changeVsStockPct > 0 ? "+" : ""}${c.changeVsStockPct.toFixed(0)}` })}</span>}
        </div>
      </div>
      <div className="text-right"><EstimateValue e={c.load} size="sm" /><div className="text-[10px] text-fg-subtle">{t("risks.limit", { v: t.val(fmtParam(c.limit)) })}</div></div>
      <div className="flex justify-end">{na ? <Badge tone="unknown">{t("common.na")}</Badge> : <SeverityBadge severity={c.severity} />}</div>
      <div className="flex justify-end"><ConfidenceBadge score={c.confidence} /></div>
    </button>
  );
}
