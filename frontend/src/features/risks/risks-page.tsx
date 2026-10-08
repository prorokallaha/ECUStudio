"use client";
import Link from "next/link";
import type { AnalysisReport } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Card, CardHeader, ConfidenceBadge, EstimateValue, KV, SectionTitle, SeverityBadge, SourceBadge } from "@/components/ui";
import { MarginRow } from "@/components/risk/margin-row";
import { EvidenceList } from "@/components/ai/answer-card";
import { useWorkspace } from "@/hooks/use-workspace";
import { useSelection } from "@/stores/selection";
import { fmtParam, severityRank } from "@/lib/format";
import { severityTone, toneText } from "@/lib/colors";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

export function RisksPage() {
  return <WithReport>{(r) => <Risks r={r} />}</WithReport>;
}

function Risks({ r }: { r: AnalysisReport }) {
  const t = useT();
  const { params, setParam, href } = useWorkspace();
  const select = useSelection((s) => s.select);
  const risk = r.risk;
  const comps = [...risk.components].sort((a, b) => severityRank(b.severity) - severityRank(a.severity));
  const activeId = params.get("item") ?? comps[0]?.component;
  const c = risk.components.find((x) => x.component === activeId);
  const findings = [...risk.findings, ...r.calibrationFindings].sort((a, b) => severityRank(b.severity) - severityRank(a.severity));
  return (
    <div className="flex h-full flex-col">
      <PageHeader title={t("nav.risks")} subtitle={t("risks.subtitle")} />
      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1 overflow-y-auto">
          <div className="flex items-center gap-4 border-b border-border px-4 py-3">
            <div className={cn("text-2xl font-bold", toneText[severityTone[risk.overall]])}>{t.tx(`severity.${risk.overall}`, risk.overall.toUpperCase())}</div>
            <ConfidenceBadge score={risk.confidence} />
            <div className="text-xs text-fg-muted">{risk.overallExplanation}</div>
          </div>
          <div className="grid grid-cols-[minmax(140px,190px)_minmax(160px,1fr)_130px_84px_96px] gap-3 border-b border-border bg-panel-2 px-3 py-1.5 text-[10px] uppercase tracking-wide text-fg-subtle">
            <span>{t("risks.colComponent")}</span><span>{t("risks.colLoadVsLimit")}</span><span className="text-right">{t("risks.colEstLoad")}</span><span className="text-right">{t("risks.colSeverity")}</span><span className="text-right">{t("common.confidence")}</span>
          </div>
          {comps.map((m) => <MarginRow key={m.component} c={m} active={m.component === activeId} onClick={() => { setParam("item", m.component); select({ kind: "component", component: m.component, label: m.label }); }} />)}
          <Card className="m-3">
            <CardHeader title={t("risks.findings")} subtitle={`${findings.length}`} />
            {findings.map((f, i) => (
              <div key={i} className="border-t border-border px-3 py-2 text-xs">
                <div className="flex items-center gap-2"><SeverityBadge severity={f.severity} /><span className="num text-[10px] text-fg-subtle">{f.code}</span><span className="text-[10px] text-fg-subtle">{t.tx(`findingSource.${f.source}`, f.source)}</span><ConfidenceBadge score={f.confidence} className="ml-auto" /></div>
                <div className="mt-1">{f.text}</div>
                {f.evidence.length > 0 && <div className="mt-1"><EvidenceList evidence={f.evidence} /></div>}
                {f.relatedMaps.length > 0 && <div className="mt-1 flex gap-2 text-[11px]">{f.relatedMaps.map((m) => <Link key={m} className="text-calc hover:underline" href={href(`maps/${m}`)}>{m}</Link>)}</div>}
              </div>
            ))}
          </Card>
        </div>
        {c && (
          <aside className="w-[340px] shrink-0 space-y-3 overflow-y-auto border-l border-border bg-bg-elev p-3 text-xs">
            <div className="flex items-center gap-2"><div className="text-sm font-semibold">{c.label}</div><SeverityBadge severity={c.severity} /></div>
            <p className="leading-relaxed text-fg-muted">{c.explanation}</p>
            <div className="divide-y divide-border">
              <KV k={t("risks.metric")}>{c.metric}</KV>
              <KV k={t("risks.estLoad")}><EstimateValue e={c.load} size="sm" /></KV>
              <KV k={t("risks.knownLimit")}><span className={fmtParam(c.limit) === "UNKNOWN" ? "font-semibold text-unknown" : "num"}>{t.val(fmtParam(c.limit))}</span></KV>
              {c.limit && fmtParam(c.limit) !== "UNKNOWN" && <KV k={t("risks.limitSource")}><SourceBadge source={c.limit.source} /></KV>}
              {c.showExactUtilization ? <KV k={t("risks.utilization")}><EstimateValue e={c.utilization} size="sm" /></KV> : <KV k={t("risks.utilization")}><span className="text-unknown">{t("risks.notShown")}</span></KV>}
              {c.showExactUtilization && <KV k={t("risks.margin")}><EstimateValue e={c.margin} size="sm" /></KV>}
              <KV k={t("risks.loadLevel")}>{t.tx(`loadLevel.${c.loadLevel}`, c.loadLevel)}</KV>
              {c.affectedRpm && <KV k={t("risks.affectedRpm")}><span className="num">{c.affectedRpm[0]}–{c.affectedRpm[1]}</span></KV>}
            </div>
            {!!c.relatedMaps?.length && <div><SectionTitle className="mb-1">{t("risks.relatedMaps")}</SectionTitle><div className="flex flex-wrap gap-1.5">{c.relatedMaps.map((m) => <Link key={m} href={href(`maps/${m}`)} className="rounded border border-border px-1.5 py-0.5 text-calc hover:bg-panel-2">{m}</Link>)}</div></div>}
            {!!c.missingData?.length && <div><SectionTitle className="mb-1">{t("risks.missingData")}</SectionTitle>{c.missingData.map((m, i) => <div key={i} className="text-unknown">• {m}</div>)}</div>}
            {!!c.evidence?.length && <div><SectionTitle className="mb-1">{t("risks.evidence")}</SectionTitle><EvidenceList evidence={c.evidence} /></div>}
            <Link href={href("vehicle")} className="block text-calc hover:underline">{t("risks.overrideLink")}</Link>
          </aside>
        )}
      </div>
    </div>
  );
}
