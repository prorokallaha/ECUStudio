"use client";
import { useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { Download, Printer } from "lucide-react";
import type { AnalysisReport } from "@/types/domain";
import { api } from "@/services/api";
import { keys, useProject } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Button, Card, CardHeader, EstimateValue, Select, SeverityBadge } from "@/components/ui";
import { estimateString, fmtDate, fmtParam } from "@/lib/format";

export function ReportsPage() {
  return <WithReport>{(r) => <Reports r={r} />}</WithReport>;
}

function Reports({ r }: { r: AnalysisReport }) {
  const { projectId } = useWorkspace();
  const project = useProject(projectId);
  const versions = (project.data?.files ?? []).filter((f) => f.summary);
  const [a, setA] = useState<string>(versions.find((v) => v.role === "Modified")?.summary?.analysisId ?? r.id);
  const [b, setB] = useState<string>(versions.find((v) => v.summary?.analysisId !== a)?.summary?.analysisId ?? "");
  const cmp = useQueries({ queries: [a, b].filter(Boolean).map((id) => ({ queryKey: keys.report(id), queryFn: () => api.analyses.report(id), staleTime: Infinity })) });
  const [ra, rb] = cmp.map((q) => q.data);

  return (
    <div>
      <PageHeader title="Reports" actions={<>
        <Button size="sm" onClick={() => window.print()}><Printer className="size-3.5" />Print / PDF</Button>
        <Button size="sm" variant="primary" onClick={() => window.open(api.analyses.reportMarkdownUrl(r.id), "_blank")}><Download className="size-3.5" />Markdown</Button>
      </>} />
      <div className="space-y-4 p-4">
        <Card>
          <CardHeader title="Compare versions" subtitle="any two analysed binaries of this project" />
          <div className="flex items-center gap-2 border-b border-border px-3 py-2 text-xs">
            <Select value={a} onChange={(e) => setA(e.target.value)}>{versions.map((v) => <option key={v.id} value={v.summary!.analysisId}>{v.label}</option>)}</Select>
            <span className="text-fg-subtle">vs</span>
            <Select value={b} onChange={(e) => setB(e.target.value)}><option value="">—</option>{versions.map((v) => <option key={v.id} value={v.summary!.analysisId}>{v.label}</option>)}</Select>
          </div>
          {ra && (
            <table className="w-full text-xs">
              <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>Metric</th><th>{ra.modifiedName}</th>{rb && <th>{rb.modifiedName}</th>}</tr></thead>
              <tbody>
                {ra.keyMetrics.map((m) => (
                  <tr key={m.id} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5">
                    <td className="text-fg-muted">{m.label}</td><td><EstimateValue e={m.modified} size="sm" /></td>
                    {rb && <td><EstimateValue e={rb.keyMetrics.find((x) => x.id === m.id)?.modified} size="sm" /></td>}
                  </tr>
                ))}
                <tr className="border-t border-border [&>td]:px-3 [&>td]:py-1.5"><td className="text-fg-muted">Overall risk</td><td><SeverityBadge severity={ra.risk.overall} /></td>{rb && <td><SeverityBadge severity={rb.risk.overall} /></td>}</tr>
                <tr className="border-t border-border [&>td]:px-3 [&>td]:py-1.5"><td className="text-fg-muted">Modified maps</td><td>{ra.modifiedMaps?.length ?? 0}</td>{rb && <td>{rb.modifiedMaps?.length ?? 0}</td>}</tr>
              </tbody>
            </table>
          )}
        </Card>

        <article className="mx-auto max-w-4xl rounded-lg border border-border bg-panel p-8 text-[13px] leading-relaxed print:border-0 print:p-0">
          <h1 className="text-xl font-bold">ECUStudio analysis report</h1>
          <div className="mt-1 text-xs text-fg-muted">{fmtDate(r.createdAt)} · analysis {r.analysisVersion} · {r.id}</div>
          <p className="mt-3 rounded border border-attn/30 bg-attn/10 p-2 text-xs">{r.disclaimer}</p>
          <h2 className="mt-5 text-sm font-semibold uppercase tracking-wide text-fg-muted">Vehicle & ECU</h2>
          <div className="mt-1 grid grid-cols-2 gap-x-8 text-xs">
            <div>Vehicle: {fmtParam(r.vehicle.profile.make)} {fmtParam(r.vehicle.profile.model)}</div><div>Engine: {fmtParam(r.vehicle.profile.engineCode)}</div>
            <div>ECU: {r.ecu.ecuFamily}</div><div>SW: {fmtParam(r.ecu.softwareNumber)}</div>
            <div>Files: {r.modifiedName}{r.stockName ? ` vs ${r.stockName}` : ""}</div><div>Checksums: {r.checksums.overall}</div>
          </div>
          <h2 className="mt-5 text-sm font-semibold uppercase tracking-wide text-fg-muted">Key metrics</h2>
          <table className="mt-1 w-full text-xs"><tbody>{r.keyMetrics.map((m) => <tr key={m.id} className="border-b border-border"><td className="py-1">{m.label}</td><td>{m.stock ? estimateString(m.stock) : "—"}</td><td className="font-semibold">{estimateString(m.modified)}</td></tr>)}</tbody></table>
          <h2 className="mt-5 text-sm font-semibold uppercase tracking-wide text-fg-muted">Risk: {r.risk.overall}</h2>
          <p className="text-xs">{r.risk.overallExplanation}</p>
          <ul className="mt-1 list-disc pl-5 text-xs">{r.risk.components.map((c) => <li key={c.component}><b>{c.label}</b> — {c.severity}: {c.explanation}</li>)}</ul>
          <h2 className="mt-5 text-sm font-semibold uppercase tracking-wide text-fg-muted">Findings</h2>
          <ul className="list-disc pl-5 text-xs">{r.calibrationFindings.map((f, i) => <li key={i}><b>{f.severity}</b> {f.text}</li>)}</ul>
          <h2 className="mt-5 text-sm font-semibold uppercase tracking-wide text-fg-muted">Unknowns and recommended checks</h2>
          <ul className="list-disc pl-5 text-xs">{[...(r.unknowns ?? []), ...(r.risk.criticalUnknowns ?? [])].map((u, i) => <li key={i}>{u}</li>)}</ul>
        </article>
      </div>
    </div>
  );
}
