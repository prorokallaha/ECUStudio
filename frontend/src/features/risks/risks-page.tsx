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

export function RisksPage() {
  return <WithReport>{(r) => <Risks r={r} />}</WithReport>;
}

function Risks({ r }: { r: AnalysisReport }) {
  const { params, setParam, href } = useWorkspace();
  const select = useSelection((s) => s.select);
  const risk = r.risk;
  const comps = [...risk.components].sort((a, b) => severityRank(b.severity) - severityRank(a.severity));
  const activeId = params.get("item") ?? comps[0]?.component;
  const c = risk.components.find((x) => x.component === activeId);
  const findings = [...risk.findings, ...r.calibrationFindings].sort((a, b) => severityRank(b.severity) - severityRank(a.severity));
  return (
    <div className="flex h-full flex-col">
      <PageHeader title="Risks" subtitle="Limits come only from the hardware profile with their source. Unknown limit ⇒ UNKNOWN, never SAFE." />
      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1 overflow-y-auto">
          <div className="flex items-center gap-4 border-b border-border px-4 py-3">
            <div className={cn("text-2xl font-bold", toneText[severityTone[risk.overall]])}>{risk.overall.toUpperCase()}</div>
            <ConfidenceBadge score={risk.confidence} />
            <div className="text-xs text-fg-muted">{risk.overallExplanation}</div>
          </div>
          <div className="grid grid-cols-[minmax(140px,190px)_minmax(160px,1fr)_130px_84px_96px] gap-3 border-b border-border bg-panel-2 px-3 py-1.5 text-[10px] uppercase tracking-wide text-fg-subtle">
            <span>Component</span><span>Load vs limit</span><span className="text-right">Estimated load</span><span className="text-right">Severity</span><span className="text-right">Confidence</span>
          </div>
          {comps.map((m) => <MarginRow key={m.component} c={m} active={m.component === activeId} onClick={() => { setParam("item", m.component); select({ kind: "component", component: m.component, label: m.label }); }} />)}
          <Card className="m-3">
            <CardHeader title="Findings" subtitle={`${findings.length}`} />
            {findings.map((f, i) => (
              <div key={i} className="border-t border-border px-3 py-2 text-xs">
                <div className="flex items-center gap-2"><SeverityBadge severity={f.severity} /><span className="num text-[10px] text-fg-subtle">{f.code}</span><span className="text-[10px] text-fg-subtle">{f.source}</span><ConfidenceBadge score={f.confidence} className="ml-auto" /></div>
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
              <KV k="Metric">{c.metric}</KV>
              <KV k="Estimated load"><EstimateValue e={c.load} size="sm" /></KV>
              <KV k="Known limit"><span className={fmtParam(c.limit) === "UNKNOWN" ? "font-semibold text-unknown" : "num"}>{fmtParam(c.limit)}</span></KV>
              {c.limit && fmtParam(c.limit) !== "UNKNOWN" && <KV k="Limit source"><SourceBadge source={c.limit.source} /></KV>}
              {c.showExactUtilization ? <KV k="Utilization"><EstimateValue e={c.utilization} size="sm" /></KV> : <KV k="Utilization"><span className="text-unknown">not shown — insufficient data</span></KV>}
              {c.showExactUtilization && <KV k="Margin"><EstimateValue e={c.margin} size="sm" /></KV>}
              <KV k="Load level">{c.loadLevel}</KV>
              {c.affectedRpm && <KV k="Affected RPM"><span className="num">{c.affectedRpm[0]}–{c.affectedRpm[1]}</span></KV>}
            </div>
            {!!c.relatedMaps?.length && <div><SectionTitle className="mb-1">Related maps</SectionTitle><div className="flex flex-wrap gap-1.5">{c.relatedMaps.map((m) => <Link key={m} href={href(`maps/${m}`)} className="rounded border border-border px-1.5 py-0.5 text-calc hover:bg-panel-2">{m}</Link>)}</div></div>}
            {!!c.missingData?.length && <div><SectionTitle className="mb-1">Missing data</SectionTitle>{c.missingData.map((m, i) => <div key={i} className="text-unknown">• {m}</div>)}</div>}
            {!!c.evidence?.length && <div><SectionTitle className="mb-1">Evidence</SectionTitle><EvidenceList evidence={c.evidence} /></div>}
            <Link href={href("vehicle")} className="block text-calc hover:underline">Know the real component? Override it on the Vehicle page →</Link>
          </aside>
        )}
      </div>
    </div>
  );
}
