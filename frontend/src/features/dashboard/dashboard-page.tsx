"use client";
import Link from "next/link";
import { AlertTriangle, ChevronRight, Info } from "lucide-react";
import type { AnalysisReport } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Card, CardBody, CardHeader, ConfidenceBadge, SectionTitle, SeverityBadge, SourceBadge } from "@/components/ui";
import { WotChart } from "@/components/simulation/wot-chart";
import { useWorkspace } from "@/hooks/use-workspace";
import { useSelection } from "@/stores/selection";
import { MetricCard } from "./metric-card";
import { severityTone, toneBg, toneText } from "@/lib/colors";
import { severityRank, fmtParam } from "@/lib/format";
import { cn } from "@/lib/cn";

export function DashboardPage() {
  return <WithReport>{(r) => <Dashboard r={r} />}</WithReport>;
}

function Dashboard({ r }: { r: AnalysisReport }) {
  const { href } = useWorkspace();
  const select = useSelection((s) => s.select);
  const risk = r.risk;
  const findings = [...r.mainFindings].sort((a, b) => severityRank(b.severity) - severityRank(a.severity));
  const profile = r.vehicle.profile;
  return (
    <div className="flex flex-col">
      <PageHeader
        title="Dashboard"
        subtitle={`${r.modifiedName}${r.stockName ? ` vs ${r.stockName}` : " (no stock file — absolute values only)"} · analysis ${r.analysisVersion}`}
        actions={<span className="text-[11px] text-fg-subtle">{r.disclaimer}</span>}
      />
      <div className="space-y-4 p-4">
        <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
          {r.keyMetrics.map((m) => <MetricCard key={m.id} m={m} href={m.link ? href(m.link) : undefined} />)}
          <Link href={href("risks")} className="relative overflow-hidden rounded-lg border border-border bg-panel p-3 hover:border-border-strong">
            <span className={cn("absolute inset-y-0 left-0 w-0.5", toneBg[severityTone[risk.overall]])} />
            <div className="text-[11px] font-medium uppercase tracking-wide text-fg-subtle">Overall risk</div>
            <div className={cn("mt-2 text-xl font-bold", toneText[severityTone[risk.overall]])}>{risk.overall.toUpperCase()}</div>
            <div className="mt-1 line-clamp-2 text-[11px] text-fg-muted">{risk.overallExplanation}</div>
          </Link>
          <div className="rounded-lg border border-border bg-panel p-3">
            <div className="text-[11px] font-medium uppercase tracking-wide text-fg-subtle">Confidence</div>
            <div className="mt-2 flex items-center gap-2"><ConfidenceBadge score={risk.confidence} /><span className="text-[11px] text-fg-muted">data {r.dataAvailability.level}</span></div>
            <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-panel-2"><div className="h-full bg-calc" style={{ width: `${r.dataAvailability.score * 100}%` }} /></div>
            <div className="mt-1.5 text-[11px] text-fg-subtle">More data (stock, definitions, logs, verified hardware) narrows every range.</div>
          </div>
        </div>

        <div className="grid grid-cols-1 gap-4 xl:grid-cols-5">
          <Card className="xl:col-span-3">
            <CardHeader title="Main findings" subtitle={`${findings.length} items`} icon={<AlertTriangle className="size-3.5" />} />
            <div className="divide-y divide-border">
              {findings.map((f, i) => (
                <Link
                  key={i}
                  href={f.link ? href(f.link) : href("risks")}
                  onClick={() => select({ kind: "finding", code: f.code, text: f.text })}
                  className="flex items-center gap-3 px-3 py-2 text-xs hover:bg-panel-2"
                >
                  <SeverityBadge severity={f.severity} className="w-[70px] justify-center" />
                  <span className="flex-1">{f.text}</span>
                  <span className="num text-[10px] text-fg-subtle">{f.code}</span>
                  <ChevronRight className="size-3.5 text-fg-subtle" />
                </Link>
              ))}
            </div>
          </Card>
          <Card className="xl:col-span-2">
            <CardHeader title="Full-load estimate" subtitle="4th gear · 20 °C · sea level" actions={<Link href={href("dyno")} className="text-[11px] text-calc hover:underline">Open Virtual Dyno</Link>} />
            <div className="h-64 p-2"><WotChart mod={r.simulation.modifiedWot} stock={r.simulation.stockWot} /></div>
          </Card>
        </div>

        <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
          <Card>
            <CardHeader title="Identification" />
            <CardBody className="space-y-1 text-xs">
              <Row k="Vehicle" v={`${fmtParam(profile.make)} ${fmtParam(profile.model)}`} src={profile.model?.source} />
              <Row k="Engine code" v={fmtParam(profile.engineCode)} src={profile.engineCode?.source} />
              <Row k="ECU" v={r.ecu.ecuFamily} src="EcuBinary" />
              <Row k="Definitions" v={r.definitionSource} />
              <Row k="Checksums" v={r.checksums.overall} warn={r.checksums.overall !== "Valid"} />
            </CardBody>
          </Card>
          <Card>
            <CardHeader title="Critical unknowns" icon={<Info className="size-3.5" />} />
            <CardBody className="space-y-1.5">
              {risk.criticalUnknowns?.length ? risk.criticalUnknowns.map((u, i) => <div key={i} className="text-xs text-unknown">• {u}</div>) : <div className="text-xs text-fg-muted">None — all critical limits known.</div>}
            </CardBody>
          </Card>
          <Card>
            <CardHeader title="Data availability" />
            <CardBody className="space-y-1">
              {r.dataAvailability.factors.map((f, i) => <div key={i} className="text-xs text-fg-muted">• {f}</div>)}
            </CardBody>
          </Card>
        </div>
        <SectionTitle className="pt-2">Assumptions used by the model</SectionTitle>
        <ul className="grid grid-cols-1 gap-x-6 gap-y-0.5 text-[11px] text-fg-muted md:grid-cols-2">
          {(r.simulation.assumptions ?? []).map((a, i) => <li key={i}>• {a}</li>)}
        </ul>
      </div>
    </div>
  );
}

function Row({ k, v, src, warn }: { k: string; v: string; src?: any; warn?: boolean }) {
  return (
    <div className="flex items-center justify-between gap-2">
      <span className="text-fg-subtle">{k}</span>
      <span className="flex items-center gap-1.5 truncate">
        <span className={cn("truncate", warn && "text-warn")}>{v}</span>
        {src && <SourceBadge source={src} />}
      </span>
    </div>
  );
}
