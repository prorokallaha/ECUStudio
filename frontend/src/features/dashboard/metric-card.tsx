"use client";
import Link from "next/link";
import { ArrowRight } from "lucide-react";
import type { KeyMetric } from "@/types/domain";
import { EstimateValue, SeverityBadge } from "@/components/ui";
import { fmtEstimate } from "@/lib/format";
import { severityTone, toneBg } from "@/lib/colors";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

export function MetricCard({ m, href }: { m: KeyMetric; href?: string }) {
  const t = useT();
  const tone = m.severity ? severityTone[m.severity] : null;
  const delta = m.stock?.value && m.modified?.value ? ((m.modified.value - m.stock.value) / m.stock.value) * 100 : null;
  const body = (
    <div className="group relative h-full overflow-hidden rounded-lg border border-border bg-panel p-3 transition-colors hover:border-border-strong">
      {tone && <span className={cn("absolute inset-y-0 left-0 w-0.5", toneBg[tone])} />}
      <div className="flex items-center justify-between">
        <span className="text-[11px] font-medium uppercase tracking-wide text-fg-subtle">{t.tx(`dashboard.metric.${m.id}`, m.label)}</span>
        {m.severity && <SeverityBadge severity={m.severity} />}
      </div>
      <div className="mt-2"><EstimateValue e={m.modified} size="lg" compact /></div>
      <div className="mt-0.5 h-4 text-[11px] text-fg-subtle num">{fmtEstimate(m.modified).range ? t("dashboard.range", { r: fmtEstimate(m.modified).range }) : ""}</div>
      <div className="mt-1.5 flex items-center gap-1.5 text-[11px]">
        {m.stock ? (
          <>
            <span className="text-fg-subtle">{t("dashboard.stock")}</span>
            <EstimateValue e={m.stock} size="sm" compact className="text-fg-muted" />
            {delta !== null && Math.abs(delta) >= 0.5 && <span className={cn("num ml-auto", delta > 0 ? "text-warn" : "text-calc")}>{delta > 0 ? "+" : ""}{delta.toFixed(0)}%</span>}
          </>
        ) : (
          <span className="truncate text-fg-muted" title={m.detail ?? undefined}>{m.detail ?? ""}</span>
        )}
      </div>
      {href && <ArrowRight className="absolute right-2 bottom-2 size-3 text-fg-subtle opacity-0 transition-opacity group-hover:opacity-100" />}
    </div>
  );
  return href ? <Link href={href}>{body}</Link> : body;
}
