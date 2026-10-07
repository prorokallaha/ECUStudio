"use client";
import { useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Play, ScrollText, Trash2, Upload } from "lucide-react";
import { toast } from "sonner";
import type { LogAgreement, LogValidation, Project } from "@/types/domain";
import { api, ApiError } from "@/services/api";
import { keys, useProject, useReport, useRunAnalysis } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useFileDrop } from "@/hooks/use-file-drop";
import { PageHeader, PageSkeleton } from "@/components/layout/page";
import { Badge, Button, Card, CardHeader, EmptyState, KV, Select } from "@/components/ui";
import { LogChannelChart } from "@/components/simulation/log-chart";
import { fmtBytes, fmtDate } from "@/lib/format";
import type { Tone } from "@/lib/colors";
import { cn } from "@/lib/cn";

const agreementTone: Record<LogAgreement, Tone> = { Agrees: "ok", Deviates: "warn", Insufficient: "unknown" };

export function LogsPage() {
  const { projectId } = useWorkspace();
  const project = useProject(projectId);
  const report = useReport();
  const run = useRunAnalysis();
  if (!projectId) return <EmptyState icon={<ScrollText className="size-8" />} title="Open a project first" />;
  if (project.isLoading || !project.data) return <PageSkeleton />;
  const p = project.data;
  const validated = new Set((report.data?.logs ?? []).map((l) => l.logId));
  const stale = p.logs.some((l) => !validated.has(l.id));
  return (
    <div>
      <PageHeader
        title="Logs"
        subtitle="Diagnostic logs compared with the steady-state model at full load"
        actions={stale && p.files.length > 0 && (
          <Button size="xs" variant="primary" disabled={run.isPending} onClick={() => run.mutate({ projectId: p.id })}><Play className="size-3" />Re-run analysis to validate</Button>
        )}
      />
      <div className="space-y-3 p-4">
        <LogsCard project={p} validated={validated} />
        {(report.data?.logs ?? []).map((v) => <ValidationCard key={v.logId} v={v} />)}
        {p.logs.length > 0 && (report.data?.logs ?? []).length === 0 && (
          <div className="rounded-md border border-dashed border-border px-4 py-3 text-xs text-fg-muted">
            Logs are compared with the model during analysis. Run the analysis for the binary each log was recorded with.
          </div>
        )}
      </div>
    </div>
  );
}

function LogsCard({ project, validated }: { project: Project; validated: Set<string> }) {
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const defaultFile = project.files.findLast((f) => f.role === "Modified")?.id ?? project.files[0]?.id ?? "";
  const [fileId, setFileId] = useState<string>(defaultFile);
  const refresh = () => qc.invalidateQueries({ queryKey: keys.project(project.id) });
  const upload = async (files: File[]) => {
    for (const f of files) {
      try {
        const log = await api.projects.uploadLog(project.id, f, fileId || undefined);
        toast.success(`${f.name}: ${log.samples} samples, ${log.channels.length} channels`);
      } catch (e) {
        toast.error(e instanceof ApiError ? `${f.name}: ${e.message}` : String(e));
      }
    }
    refresh();
  };
  const remove = async (logId: string) => {
    await api.projects.deleteLog(project.id, logId);
    refresh();
  };
  const { over, bind } = useFileDrop(upload);
  const fileName = (id?: string | null) => project.files.find((f) => f.id === id)?.label ?? "unassigned (compared with the analysed file)";
  return (
    <Card className={cn(over && "ring-2 ring-calc")} {...bind}>
      <CardHeader
        title="Diagnostic logs"
        subtitle="VCDS measuring-block CSV or generic CSV · drop files here"
        actions={<>
          <span className="text-[10px] uppercase tracking-wide text-fg-subtle">Recorded with</span>
          <Select value={fileId} onChange={(e) => setFileId(e.target.value)} className="h-6 max-w-48">
            <option value="">unassigned</option>
            {project.files.map((f) => <option key={f.id} value={f.id}>{f.label}</option>)}
          </Select>
          <input ref={input} type="file" accept=".csv,.txt" multiple hidden onChange={(e) => { if (e.target.files) upload(Array.from(e.target.files)); e.target.value = ""; }} />
          <Button size="xs" onClick={() => input.current?.click()}><Upload className="size-3" />Upload log</Button>
        </>}
      />
      {project.logs.length === 0 ? (
        <div className="px-4 py-6 text-xs text-fg-muted">
          No logs yet. Best input: a 3rd or 4th gear full-load pull from ~1500 rpm to the limiter with engine speed, accelerator position,
          boost (specified/actual), air mass (specified/actual), injection quantity and atmospheric pressure. A log that agrees with the model
          raises data availability; one that disagrees is reported and does not.
        </div>
      ) : (
        <table className="w-full text-xs">
          <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
            <tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>Log</th><th>Format</th><th>Samples</th><th>Channels</th><th>Recorded with</th><th>Uploaded</th><th /></tr>
          </thead>
          <tbody>
            {project.logs.map((l) => (
              <tr key={l.id} className="border-t border-border align-top [&>td]:px-3 [&>td]:py-1.5">
                <td>
                  <div className="font-medium">{l.name}</div>
                  <div className="num text-[10px] text-fg-subtle">{fmtBytes(l.size)} · {l.sha256.slice(0, 10)}{validated.has(l.id) ? "" : " · not validated yet"}</div>
                  {l.warnings.length > 0 && <div className="mt-0.5 text-[10px] text-attn">{l.warnings.length} warning(s): {l.warnings[0]}</div>}
                </td>
                <td>{l.format}</td>
                <td className="num">{l.samples}</td>
                <td><div className="flex max-w-80 flex-wrap gap-1">{l.channels.map((c) => <Badge key={c} tone="calc">{c}</Badge>)}</div></td>
                <td className="text-fg-muted">{fileName(l.fileId)}</td>
                <td className="text-fg-muted">{fmtDate(l.uploadedAt)}</td>
                <td className="text-right"><Button size="xs" variant="ghost" onClick={() => remove(l.id)} title="Remove log"><Trash2 className="size-3" /></Button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

function ValidationCard({ v }: { v: LogValidation }) {
  return (
    <Card>
      <CardHeader
        title={<span className="flex items-center gap-2">{v.name}<Badge tone={agreementTone[v.status]}>{v.status}</Badge></span>}
        subtitle={`compared with the ${v.againstStock ? "stock" : "modified"} calibration`}
      />
      <div className="grid gap-4 p-3 @container lg:grid-cols-[280px_1fr]">
        <div className="space-y-2 text-xs">
          <p className="text-fg-muted">{v.explanation}</p>
          <KV k="Full-load samples">{v.wotSamples} of {v.samples}</KV>
          <KV k="Full-load criterion">{v.wotCriterion}</KV>
          <KV k="Atmospheric pressure">{v.atmosphericPressureMbar != null ? `${v.atmosphericPressureMbar.toFixed(0)} mbar (from log)` : "1013 mbar (assumed)"}</KV>
          <KV k="Peak boost (logged)">{v.peaks.boostMbar != null ? `${v.peaks.boostMbar.toFixed(0)} mbar` : "—"}</KV>
          <KV k="Peak air mass (logged)">{v.peaks.mafMg != null ? `${v.peaks.mafMg.toFixed(0)} mg/stroke` : "—"}</KV>
          <KV k="Peak IQ (logged)">{v.peaks.iqMg != null ? `${v.peaks.iqMg.toFixed(1)} mg/stroke` : "—"}</KV>
          {v.peaks.egtC != null && <KV k="Peak EGT (logged)">{v.peaks.egtC.toFixed(0)} °C</KV>}
          {v.warnings.length > 0 && (
            <div className="rounded border border-attn/30 bg-attn/8 p-2 text-[11px] text-attn">
              {v.warnings.map((w, i) => <div key={i}>• {w}</div>)}
            </div>
          )}
          {v.unmappedHeaders.length > 0 && <div className="text-[10px] text-fg-subtle">Ignored columns: {v.unmappedHeaders.slice(0, 12).join(", ")}{v.unmappedHeaders.length > 12 ? "…" : ""}</div>}
        </div>
        <div className="space-y-3">
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
              <tr className="[&>th]:px-2 [&>th]:py-1 [&>th]:text-left [&>th]:font-medium"><th>Channel</th><th>Bias vs model</th><th>Inside model range</th><th>Tolerance</th><th>Status</th><th>Note</th></tr>
            </thead>
            <tbody>
              {v.channels.map((c) => (
                <tr key={c.channel} className="border-t border-border [&>td]:px-2 [&>td]:py-1">
                  <td className="font-medium">{c.label}</td>
                  <td className="num">{c.biasPct != null ? `${c.biasPct >= 0 ? "+" : ""}${c.biasPct.toFixed(1)} %` : "—"}</td>
                  <td className="num">{c.withinRangePct != null ? `${c.withinRangePct.toFixed(0)} % of bins` : <span className="text-fg-subtle" title="ECU-requested value: the model has no range">exact value</span>}</td>
                  <td className="num text-fg-muted">±{c.tolerancePct} %</td>
                  <td><Badge tone={agreementTone[c.status]}>{c.status}</Badge></td>
                  <td className="text-fg-muted">{c.note}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="grid gap-3 @3xl:grid-cols-2">
            {v.channels.filter((c) => c.bins.some((b) => b.model != null)).map((c) => (
              <div key={c.channel} className="rounded-md border border-border p-2">
                <div className="mb-1 text-[11px] font-medium text-fg-muted">{c.label}</div>
                <LogChannelChart channel={c} className="h-48" />
              </div>
            ))}
          </div>
          <p className="text-[10px] text-fg-subtle">
            Points are medians of logged full-load samples per 250 rpm bin; the line and band are the model at the same rpm and the log’s atmospheric pressure.
            The model is steady-state, so turbo spool-up shows as low boost below ~2000 rpm.
          </p>
        </div>
      </div>
    </Card>
  );
}
