"use client";
import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Circle, Download, FileUp, Loader2, RefreshCw, XCircle } from "lucide-react";
import { toast } from "sonner";
import type { AnalysisReport, Project } from "@/types/domain";
import type { AcquisitionCandidate, DefinitionAcquisition, DefinitionCompatibilityResult, DefinitionFit } from "@/types/library";
import { toneText, type Tone } from "@/lib/colors";
import { Badge, Button, Card, CardBody, CardHeader, Dialog, Spinner, StepBar } from "@/components/ui";
import { DefinitionImportDialog } from "@/features/library/definition-import-dialog";
import { useAcquisition, useStartAcquisition, isAcquiring, type AcquisitionLive } from "@/hooks/use-acquisition";
import { keys } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { fmtBytes, fmtParam } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

type T = ReturnType<typeof useT>;
const baseName = (f: string) => f.replace(/\.[^.]+$/, "");
const fitTone: Record<DefinitionFit, Tone> = { Exact: "ok", Compatible: "ok", Partial: "attn", Incompatible: "danger", Unknown: "unknown" };
const verified = (v?: DefinitionCompatibilityResult | null) => v?.status === "Exact" || v?.status === "Compatible";
const STAGES = ["identify", "waiting_metadata", "local_search", "torrent_search", "downloading", "verifying", "importing", "matching_maps"] as const;

function fmtEta(s?: number | null) {
  if (s === undefined || s === null || !Number.isFinite(s) || s <= 0) return null;
  if (s < 60) return `${Math.round(s)} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`;
  return `${Math.floor(s / 3600)} h ${Math.round((s % 3600) / 60)} min`;
}

/** One line for the top bar: what definition the maps come from right now. */
function summary(t: T, project?: Project, a?: DefinitionAcquisition | null, live?: AcquisitionLive): { text: string; tone: Tone; busy?: boolean } {
  if (isAcquiring(a)) {
    const tr = live?.transfer ?? a?.transfer;
    switch (a!.state) {
      case "Downloading": return { text: t("acq.downloading", { pct: tr && tr.bytesTotal > 0 ? Math.round((100 * tr.bytesDone) / tr.bytesTotal) : 0 }), tone: "calc", busy: true };
      case "WaitingMetadata": return { text: t("acq.metadata"), tone: "calc", busy: true };
      case "Verifying": return { text: t("acq.verifying"), tone: "calc", busy: true };
      case "Importing": return { text: t("acq.importing"), tone: "calc", busy: true };
      case "MatchingMaps": return { text: t("acq.matching"), tone: "calc", busy: true };
      default: return { text: t("acq.searching"), tone: "calc", busy: true };
    }
  }
  const def = project?.definition;
  if (def) {
    const v = def.verification;
    const label = v && !verified(v) ? `${t(`acq.fit.${v.status}`)} ${v.score} %` : t("acq.verified");
    return { text: `${baseName(def.name)} ${def.format} ${label}`, tone: v ? fitTone[v.status] : "ok" };
  }
  if (a?.state === "Done" && a.chosen) {
    const v = a.verification;
    const label = v && !verified(v) ? `${t(`acq.fit.${v.status}`)} ${v.score} %` : t("acq.verified");
    return { text: `${baseName(a.chosen.fileName)} ${a.chosen.format} ${label}`, tone: v ? fitTone[v.status] : "ok" };
  }
  if (a?.state === "AwaitingConfirmation" && a.candidates[0]) return { text: t("acq.probable", { name: baseName(a.candidates[0].fileName) }), tone: "attn" };
  if (a?.state === "NotFound" && a.reason === "NotIndexed") return { text: t("acq.waitingIndex"), tone: "calc" };
  if (a?.state === "NotFound") return { text: t("acq.notFound"), tone: "unknown" };
  if (a?.state === "Failed" && a.reason === "DownloadFailed") return { text: t("acq.notDownloaded"), tone: "danger" };
  if (a?.state === "Failed") return { text: t("acq.failed"), tone: "danger" };
  return { text: t("acq.none"), tone: "unknown" };
}

/** Top-bar "Definition" field; click opens the search details. Also announces a finished search. */
export function DefinitionStatus({ project, report }: { project?: Project; report?: AnalysisReport }) {
  const t = useT();
  const qc = useQueryClient();
  const { setParam } = useWorkspace();
  const acq = useAcquisition(project?.id);
  const [open, setOpen] = useState(false);
  const a = acq.data;
  const s = summary(t, project, a, acq.live);

  // Announce transitions that happen while the user is in the workspace.
  const prev = useRef<{ id?: string; state?: string }>({});
  useEffect(() => {
    const before = prev.current;
    prev.current = { id: project?.id, state: a?.state };
    if (!project || !a || before.id !== project.id || !before.state || before.state === a.state) return;
    if (a.state === "Done") {
      qc.invalidateQueries({ queryKey: keys.project(project.id) });
      qc.invalidateQueries({ queryKey: keys.projects });
      if (a.analysisId) setParam("a", a.analysisId);
      const v = a.verification;
      toast.success(t("acq.connected", { file: a.chosen?.fileName ?? "" }), {
        description: v ? t("acq.connectedDetail", { score: v.score, t: v.tables, a: v.axes, p: v.parameters }) : undefined,
        action: { label: t("acq.open"), onClick: () => setOpen(true) },
        duration: 10_000,
      });
    } else if (a.state === "AwaitingConfirmation" && a.candidates[0]) {
      toast(t("acq.probableToast", { file: a.candidates[0].fileName }), { action: { label: t("acq.open"), onClick: () => setOpen(true) }, duration: 15_000 });
    }
  }, [a, project, qc, setParam, t]);

  if (!project) return null;
  return (
    <>
      <button
        className="flex min-w-0 max-w-[260px] flex-col justify-center border-r border-border px-2.5 text-left hover:bg-panel-2"
        onClick={() => setOpen(true)}
        title={s.text}
        data-testid="definition-status"
      >
        <span className="text-[10px] uppercase leading-none tracking-wider text-fg-subtle">{t("acq.label")}</span>
        <span className={cn("mt-0.5 flex items-center gap-1 truncate text-xs leading-tight", toneText[s.tone])}>
          {s.busy && <Spinner className="size-2.5" />}
          <span className="truncate num">{s.text}</span>
        </span>
      </button>
      <AcquisitionDialog open={open} onClose={() => setOpen(false)} project={project} report={report} acquisition={a ?? null} live={acq.live} />
    </>
  );
}

function AcquisitionDialog({ open, onClose, project, report, acquisition: a, live }: {
  open: boolean; onClose: () => void; project: Project; report?: AnalysisReport; acquisition: DefinitionAcquisition | null; live: AcquisitionLive;
}) {
  const t = useT();
  const start = useStartAcquisition(project.id);
  const busy = isAcquiring(a) || start.isPending;
  return (
    <Dialog open={open} onClose={onClose} title={t("acq.title")} className="max-w-2xl"
      footer={<Button variant="primary" disabled={busy} onClick={() => start.mutate(null)}>{busy ? <Spinner /> : <RefreshCw className="size-3.5" />}{a ? t("acq.searchAgain") : t("acq.start")}</Button>}>
      <div className="max-h-[62vh] overflow-auto" data-testid="acquisition-dialog">
        <AcquisitionPanel project={project} report={report} acquisition={a} live={live} />
      </div>
    </Dialog>
  );
}

/** ECU page: the DAMOS search of this binary, in place of a raw list of library matches. */
export function AcquisitionCard({ project, report, className }: { project: Project; report?: AnalysisReport; className?: string }) {
  const t = useT();
  const acq = useAcquisition(project.id);
  const start = useStartAcquisition(project.id);
  const a = acq.data ?? null;
  const busy = isAcquiring(a) || start.isPending;
  return (
    <Card className={className}>
      <CardHeader title={t("acq.cardTitle")} subtitle={t("acq.cardSubtitle")}
        actions={<Button size="xs" disabled={busy} onClick={() => start.mutate(null)}>{busy ? <Spinner /> : <RefreshCw className="size-3" />}{a ? t("acq.searchAgain") : t("acq.start")}</Button>} />
      <CardBody><AcquisitionPanel project={project} report={report} acquisition={a} live={acq.live} /></CardBody>
    </Card>
  );
}

function AcquisitionPanel({ project, report, acquisition: a, live }: { project: Project; report?: AnalysisReport; acquisition: DefinitionAcquisition | null; live: AcquisitionLive }) {
  const t = useT();
  const start = useStartAcquisition(project.id);
  const [manual, setManual] = useState(false);
  const busy = isAcquiring(a) || start.isPending;
  const tr = live.transfer ?? a?.transfer;
  const v = a?.verification ?? project.definition?.verification;
  const oem = report ? fmtParam(report.ecu.oemPartNumber) : "UNKNOWN";
  const yourBin = [oem !== "UNKNOWN" ? oem : null, a?.binarySoftwareVersion].filter(Boolean).join("_") || "—";
  const steps = STAGES.map((id) => live.steps.find((x) => x.step === id)).filter((x): x is NonNullable<typeof x> => !!x);
  const ended = a && (a.state === "NotFound" || a.state === "Failed");
  const top = a?.candidates[0];

  return (
      <div className="space-y-3 text-xs">
        <div className="flex items-center gap-2">
          <Badge tone={a?.state === "Done" ? "ok" : a?.state === "Failed" ? "danger" : a?.state === "AwaitingConfirmation" ? "attn" : isAcquiring(a) ? "calc" : "unknown"}>
            {a ? t.tx(`acq.state.${a.state}`, a.state) : project.definition ? t("acq.manual") : t("acq.none")}
          </Badge>
          {a?.message && a.reason !== "DownloadFailed" && <span className="text-fg-muted">{a.message}</span>}
        </div>
        {!a && !project.definition && <p className="text-fg-muted">{t("acq.heuristicHint")}</p>}
        {ended && a.reason && <p className={a.state === "Failed" ? "text-danger" : "text-fg-muted"}>{t.tx(`acq.reason.${a.reason}`, a.reason, { msg: a.message ?? "" })}</p>}
        {ended && !project.definition && (
          <div className="space-y-1.5 rounded border border-border p-2.5">
            {top && !top.available && <>
              <div className="text-fg-muted">{t("acq.manualHint")}</div>
              <div className="num select-all break-all rounded bg-bg px-2 py-1 text-[11px]">{top.path}</div>
            </>}
            <Button size="sm" onClick={() => setManual(true)}><FileUp className="size-3.5" />{t("acq.uploadManual")}</Button>
          </div>
        )}

        {isAcquiring(a) && (
          <div className="space-y-2 rounded border border-border p-2.5">
            <div className="grid grid-cols-2 gap-x-3 gap-y-1">
              {steps.map((st) => (
                <div key={st.step} className="flex min-w-0 items-center gap-1.5">
                  {st.state === "Done" ? <CheckCircle2 className="size-3 shrink-0 text-ok" /> : st.state === "Running" ? <Loader2 className="size-3 shrink-0 animate-spin text-calc" /> : st.state === "Failed" ? <XCircle className="size-3 shrink-0 text-danger" /> : <Circle className="size-3 shrink-0 text-fg-subtle" />}
                  <span className="shrink-0">{t.tx(`acq.step.${st.step}`, st.label)}</span>
                  {st.message && <span className="truncate text-fg-muted" title={st.message}>{st.message}</span>}
                </div>
              ))}
            </div>
            {tr && a?.state === "Downloading" && (
              <div className="space-y-1" data-testid="transfer">
                <div className="flex justify-between gap-2"><span className="truncate font-medium">{tr.file}</span><span className="num">{t("acq.transfer", { done: (tr.bytesDone / 1048576).toFixed(1), total: (tr.bytesTotal / 1048576).toFixed(1), pct: tr.bytesTotal ? Math.round((100 * tr.bytesDone) / tr.bytesTotal) : 0 })}</span></div>
                <StepBar fraction={tr.bytesTotal ? tr.bytesDone / tr.bytesTotal : 0} />
                <div className="flex gap-3 text-fg-muted num">
                  {tr.fromCache ? <span>{t("acq.fromCache")}</span> : <>
                    <span>{t("acq.speed", { v: fmtBytes(tr.bytesPerSecond) })}</span>
                    <span>{t("acq.peers", { p: tr.peers, s: tr.seeds })}</span>
                    {fmtEta(tr.secondsLeft) && <span>{t("acq.eta", { v: fmtEta(tr.secondsLeft) })}</span>}
                  </>}
                </div>
              </div>
            )}
          </div>
        )}

        {a?.state === "AwaitingConfirmation" && a.candidates.map((c) => (
          <div key={c.entryId} className="flex items-center gap-3 rounded border border-attn/40 bg-attn/5 p-2.5">
            <div className="min-w-0 flex-1">
              <div className="font-medium">{t("acq.probableTitle", { file: baseName(c.fileName) })}</div>
              <div className="text-fg-muted">{t("acq.yourBin", { v: yourBin })}</div>
              <div className="truncate text-fg-subtle" title={c.reasons.join("; ")}>{c.reasons.join(" · ")}</div>
            </div>
            <Button variant="primary" size="sm" disabled={busy} onClick={() => start.mutate(c.entryId)}><Download className="size-3.5" />{t("acq.downloadAndCheck")}</Button>
          </div>
        ))}

        {v && <Verification v={v} reconciled={a?.reconciledCount ?? 0} />}

        {a && a.state !== "AwaitingConfirmation" && a.candidates.length > 0 && (
          <div>
            <div className="mb-1 font-semibold">{t("acq.candidates")}</div>
            <div className="divide-y divide-border rounded border border-border">
              {a.candidates.map((c) => <CandidateRow key={c.entryId} c={c} chosen={a.chosen?.entryId === c.entryId && a.state === "Done"} disabled={busy} onPick={() => start.mutate(c.entryId)} />)}
            </div>
          </div>
        )}

        {a && a.log.length > 0 && (
          <details>
            <summary className="cursor-pointer font-semibold">{t("acq.log")}</summary>
            <pre className="mt-1 whitespace-pre-wrap rounded bg-bg p-2 text-[11px] text-fg-muted">{a.log.join("\n")}</pre>
          </details>
        )}
        <DefinitionImportDialog open={manual} onClose={() => setManual(false)} projectId={project.id} />
      </div>
  );
}

function CandidateRow({ c, chosen, disabled, onPick }: { c: AcquisitionCandidate; chosen: boolean; disabled: boolean; onPick: () => void }) {
  const t = useT();
  return (
    <div className={cn("flex items-center gap-2 px-2.5 py-1.5", chosen && "bg-ok/5")}>
      {chosen ? <CheckCircle2 className="size-3.5 shrink-0 text-ok" /> : <Circle className="size-3.5 shrink-0 text-fg-subtle" />}
      <div className="min-w-0 flex-1">
        <div className="truncate font-medium" title={c.path}>{c.fileName}</div>
        <div className="truncate text-fg-subtle" title={c.reasons.join("; ")}>{c.source} · {c.available ? t("acq.onDisk") : t("acq.inTorrent")} · {fmtBytes(c.size)} · {c.reasons.join(" · ")}</div>
      </div>
      <Badge tone="calc">{t("acq.rank", { n: c.rank })}</Badge>
      {c.confidence && <Badge tone={c.confidence === "Medium" ? "attn" : c.confidence === "Low" ? "unknown" : "ok"}>{t(`acq.conf.${c.confidence}`)}</Badge>}
      {!chosen && <Button size="sm" variant="ghost" disabled={disabled} onClick={onPick} title={t("acq.downloadAndCheck")}><Download className="size-3.5" /></Button>}
    </div>
  );
}

function Verification({ v, reconciled }: { v: DefinitionCompatibilityResult; reconciled: number }) {
  const t = useT();
  return (
    <div className="space-y-1.5 rounded border border-border p-2.5" data-testid="verification">
      <div className="flex items-center gap-2">
        <span className="font-semibold">{t("acq.verification")}</span>
        <Badge tone={fitTone[v.status]}>{t(`acq.fit.${v.status}`)}</Badge>
        <span className="num">{t("acq.score", { v: v.score })}</span>
      </div>
      <div className="text-fg-muted">
        {t("acq.counts", { t: v.tables, a: v.axes, p: v.parameters })} · {t("acq.matched", { m: v.matchedMaps, n: v.totalMaps })}
        {v.relocatedMaps > 0 && <> · {t("acq.relocated", { n: v.relocatedMaps })}</>}
        {v.invalidMaps > 0 && <> · {t("acq.invalid", { n: v.invalidMaps })}</>}
        {reconciled > 0 && <> · {t("acq.reconciled", { n: reconciled })}</>}
      </div>
      {v.evidence.length > 0 && <ul className="list-disc space-y-0.5 pl-4 text-fg-muted">{v.evidence.map((e, i) => <li key={i}>{e}</li>)}</ul>}
      {v.conflicts.length > 0 && <ul className="list-disc space-y-0.5 pl-4 text-attn">{v.conflicts.map((e, i) => <li key={i}>{e}</li>)}</ul>}
    </div>
  );
}
