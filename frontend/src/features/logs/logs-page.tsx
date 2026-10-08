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
import { useT } from "@/i18n";

const agreementTone: Record<LogAgreement, Tone> = { Agrees: "ok", Deviates: "warn", Insufficient: "unknown" };

export function LogsPage() {
  const t = useT();
  const { projectId } = useWorkspace();
  const project = useProject(projectId);
  const report = useReport();
  const run = useRunAnalysis();
  if (!projectId) return <EmptyState icon={<ScrollText className="size-8" />} title={t("logs.openProject")} />;
  if (project.isLoading || !project.data) return <PageSkeleton />;
  const p = project.data;
  const validated = new Set((report.data?.logs ?? []).map((l) => l.logId));
  const stale = p.logs.some((l) => !validated.has(l.id));
  return (
    <div>
      <PageHeader
        title={t("nav.logs")}
        subtitle={t("logs.subtitle")}
        actions={stale && p.files.length > 0 && (
          <Button size="xs" variant="primary" disabled={run.isPending} onClick={() => run.mutate({ projectId: p.id })}><Play className="size-3" />{t("logs.rerunValidate")}</Button>
        )}
      />
      <div className="space-y-3 p-4">
        <LogsCard project={p} validated={validated} />
        {(report.data?.logs ?? []).map((v) => <ValidationCard key={v.logId} v={v} />)}
        {p.logs.length > 0 && (report.data?.logs ?? []).length === 0 && (
          <div className="rounded-md border border-dashed border-border px-4 py-3 text-xs text-fg-muted">
            {t("logs.compareHint")}
          </div>
        )}
      </div>
    </div>
  );
}

function LogsCard({ project, validated }: { project: Project; validated: Set<string> }) {
  const t = useT();
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const defaultFile = project.files.findLast((f) => f.role === "Modified")?.id ?? project.files[0]?.id ?? "";
  const [fileId, setFileId] = useState<string>(defaultFile);
  const refresh = () => qc.invalidateQueries({ queryKey: keys.project(project.id) });
  const upload = async (files: File[]) => {
    for (const f of files) {
      try {
        const log = await api.projects.uploadLog(project.id, f, fileId || undefined);
        toast.success(t("logs.uploaded", { name: f.name, samples: log.samples, channels: log.channels.length }));
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
  const fileName = (id?: string | null) => project.files.find((f) => f.id === id)?.label ?? t("logs.unassigned");
  return (
    <Card className={cn(over && "ring-2 ring-calc")} {...bind}>
      <CardHeader
        title={t("logs.title")}
        subtitle={t("logs.cardSubtitle")}
        actions={<>
          <span className="text-[10px] uppercase tracking-wide text-fg-subtle">{t("logs.recordedWith")}</span>
          <Select value={fileId} onChange={(e) => setFileId(e.target.value)} className="h-6 max-w-48">
            <option value="">{t("logs.unassignedShort")}</option>
            {project.files.map((f) => <option key={f.id} value={f.id}>{f.label}</option>)}
          </Select>
          <input ref={input} type="file" accept=".csv,.txt" multiple hidden onChange={(e) => { if (e.target.files) upload(Array.from(e.target.files)); e.target.value = ""; }} />
          <Button size="xs" onClick={() => input.current?.click()}><Upload className="size-3" />{t("logs.uploadLog")}</Button>
        </>}
      />
      {project.logs.length === 0 ? (
        <div className="px-4 py-6 text-xs text-fg-muted">
          {t("logs.empty")}
        </div>
      ) : (
        <table className="w-full text-xs">
          <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
            <tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>{t("logs.colLog")}</th><th>{t("logs.colFormat")}</th><th>{t("logs.colSamples")}</th><th>{t("logs.colChannels")}</th><th>{t("logs.recordedWith")}</th><th>{t("logs.colUploaded")}</th><th /></tr>
          </thead>
          <tbody>
            {project.logs.map((l) => (
              <tr key={l.id} className="border-t border-border align-top [&>td]:px-3 [&>td]:py-1.5">
                <td>
                  <div className="font-medium">{l.name}</div>
                  <div className="num text-[10px] text-fg-subtle">{fmtBytes(l.size)} · {l.sha256.slice(0, 10)}{validated.has(l.id) ? "" : t("logs.notValidated")}</div>
                  {l.warnings.length > 0 && <div className="mt-0.5 text-[10px] text-attn">{t("logs.warnings", { n: l.warnings.length, first: l.warnings[0] })}</div>}
                </td>
                <td>{l.format}</td>
                <td className="num">{l.samples}</td>
                <td><div className="flex max-w-80 flex-wrap gap-1">{l.channels.map((c) => <Badge key={c} tone="calc">{c}</Badge>)}</div></td>
                <td className="text-fg-muted">{fileName(l.fileId)}</td>
                <td className="text-fg-muted">{fmtDate(l.uploadedAt)}</td>
                <td className="text-right"><Button size="xs" variant="ghost" onClick={() => remove(l.id)} title={t("logs.removeLog")}><Trash2 className="size-3" /></Button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

function ValidationCard({ v }: { v: LogValidation }) {
  const t = useT();
  return (
    <Card>
      <CardHeader
        title={<span className="flex items-center gap-2">{v.name}<Badge tone={agreementTone[v.status]}>{t.tx(`logs.agreement.${v.status}`, v.status)}</Badge></span>}
        subtitle={v.againstStock ? t("logs.comparedStock") : t("logs.comparedModified")}
      />
      <div className="grid gap-4 p-3 @container lg:grid-cols-[280px_1fr]">
        <div className="space-y-2 text-xs">
          <p className="text-fg-muted">{v.explanation}</p>
          <KV k={t("logs.wotSamples")}>{t("logs.wotOf", { a: v.wotSamples, b: v.samples })}</KV>
          <KV k={t("logs.wotCriterion")}>{v.wotCriterion}</KV>
          <KV k={t("logs.atm")}>{v.atmosphericPressureMbar != null ? t("logs.atmLogged", { v: v.atmosphericPressureMbar.toFixed(0) }) : t("logs.atmAssumed")}</KV>
          <KV k={t("logs.peakBoost")}>{v.peaks.boostMbar != null ? `${v.peaks.boostMbar.toFixed(0)} mbar` : "—"}</KV>
          <KV k={t("logs.peakMaf")}>{v.peaks.mafMg != null ? `${v.peaks.mafMg.toFixed(0)} mg/stroke` : "—"}</KV>
          <KV k={t("logs.peakIq")}>{v.peaks.iqMg != null ? `${v.peaks.iqMg.toFixed(1)} mg/stroke` : "—"}</KV>
          {v.peaks.egtC != null && <KV k={t("logs.peakEgt")}>{v.peaks.egtC.toFixed(0)} °C</KV>}
          {v.warnings.length > 0 && (
            <div className="rounded border border-attn/30 bg-attn/8 p-2 text-[11px] text-attn">
              {v.warnings.map((w, i) => <div key={i}>• {w}</div>)}
            </div>
          )}
          {v.unmappedHeaders.length > 0 && <div className="text-[10px] text-fg-subtle">{t("logs.ignored", { list: v.unmappedHeaders.slice(0, 12).join(", ") + (v.unmappedHeaders.length > 12 ? "…" : "") })}</div>}
        </div>
        <div className="space-y-3">
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
              <tr className="[&>th]:px-2 [&>th]:py-1 [&>th]:text-left [&>th]:font-medium"><th>{t("logs.colChannel")}</th><th>{t("logs.colBias")}</th><th>{t("logs.colInside")}</th><th>{t("logs.colTolerance")}</th><th>{t("common.status")}</th><th>{t("logs.colNote")}</th></tr>
            </thead>
            <tbody>
              {v.channels.map((c) => (
                <tr key={c.channel} className="border-t border-border [&>td]:px-2 [&>td]:py-1">
                  <td className="font-medium">{t.tx(`logs.channel.${c.channel}`, c.label)}</td>
                  <td className="num">{c.biasPct != null ? `${c.biasPct >= 0 ? "+" : ""}${c.biasPct.toFixed(1)} %` : "—"}</td>
                  <td className="num">{c.withinRangePct != null ? t("logs.ofBins", { v: c.withinRangePct.toFixed(0) }) : <span className="text-fg-subtle" title={t("logs.exactTip")}>{t("logs.exact")}</span>}</td>
                  <td className="num text-fg-muted">±{c.tolerancePct} %</td>
                  <td><Badge tone={agreementTone[c.status]}>{t.tx(`logs.agreement.${c.status}`, c.status)}</Badge></td>
                  <td className="text-fg-muted">{c.note}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="grid gap-3 @3xl:grid-cols-2">
            {v.channels.filter((c) => c.bins.some((b) => b.model != null)).map((c) => (
              <div key={c.channel} className="rounded-md border border-border p-2">
                <div className="mb-1 text-[11px] font-medium text-fg-muted">{t.tx(`logs.channel.${c.channel}`, c.label)}</div>
                <LogChannelChart channel={c} className="h-48" />
              </div>
            ))}
          </div>
          <p className="text-[10px] text-fg-subtle">
            {t("logs.footnote1")} {t("logs.footnote2")}
          </p>
        </div>
      </div>
    </Card>
  );
}
