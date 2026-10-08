"use client";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { AlertTriangle, CheckCircle2, Download, FilePlus2, XCircle } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import type { ProjectFile } from "@/types/domain";
import type { SaveCheck, SaveCheckStatus, SaveResult } from "@/types/editing";
import { Badge, Button, Dialog, Input, SectionTitle, Spinner } from "@/components/ui";
import { editingApi } from "@/services/api-editing";
import { ApiError } from "@/services/api";
import { keys } from "@/hooks/use-analysis";
import { editKeys } from "./use-edits";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

const statusIcon: Record<SaveCheckStatus, ReactNode> = {
  Pass: <CheckCircle2 className="size-3.5 shrink-0 text-ok" />,
  Warn: <AlertTriangle className="size-3.5 shrink-0 text-warn" />,
  Fail: <XCircle className="size-3.5 shrink-0 text-danger" />,
};

/**
 * Save the working buffer as a NEW project file. Runs the backend save-check first; the original is never overwritten.
 * Checksum states other than Corrected / Valid are shown as a hard warning that the file must not be written to an ECU.
 */
export function SaveDialog({ open, onClose, projectId, file, onOpenFile }: { open: boolean; onClose: () => void; projectId: string; file: ProjectFile; onOpenFile: (fileId: string) => void }) {
  const t = useT();
  const qc = useQueryClient();
  const [name, setName] = useState("");
  const [ackChecksum, setAckChecksum] = useState(false);
  const [ackCode, setAckCode] = useState(false);
  const [check, setCheck] = useState<SaveCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<SaveResult | null>(null);
  const seq = useRef(0);

  const runCheck = useCallback(async (nm: string | null, ackC: boolean, ackK: boolean) => {
    const my = ++seq.current;
    setChecking(true); setError(null);
    try {
      const c = await editingApi.saveCheck(projectId, file.id, { name: nm || null, acknowledgeChecksumRisk: ackC, acknowledgeCodeChanges: ackK });
      if (my !== seq.current) return;
      setCheck(c);
      setName((cur) => cur || c.suggestedName);
    } catch (e) {
      if (my === seq.current) setError(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e));
    } finally {
      if (my === seq.current) setChecking(false);
    }
  }, [projectId, file.id]);

  useEffect(() => {
    if (!open) return;
    setName(""); setAckChecksum(false); setAckCode(false); setCheck(null); setResult(null); setError(null);
    runCheck(null, false, false);
  }, [open, runCheck]);

  const setAck = (kind: "checksum" | "code", v: boolean) => {
    const c = kind === "checksum" ? v : ackChecksum, k = kind === "code" ? v : ackCode;
    if (kind === "checksum") setAckChecksum(v); else setAckCode(v);
    runCheck(name, c, k);
  };

  const needs = check?.needsAcknowledgement ?? [];
  const acksDone = (!needs.includes("checksum") || ackChecksum) && (!needs.includes("code") || ackCode);
  const nameOk = /\S/.test(name) && !/[\\/]/.test(name);

  const save = async () => {
    setSaving(true); setError(null);
    try {
      const r = await editingApi.save(projectId, file.id, { name: name.trim(), acknowledgeChecksumRisk: ackChecksum, acknowledgeCodeChanges: ackCode });
      setResult(r);
      qc.invalidateQueries({ queryKey: keys.project(projectId) });
      qc.invalidateQueries({ queryKey: keys.projects });
      qc.invalidateQueries({ queryKey: editKeys.state(projectId, file.id) });
    } catch (e) {
      if (e instanceof ApiError && e.code === "SAVE_BLOCKED") {
        const c = (e.details as { check?: SaveCheck } | undefined)?.check;
        if (c) setCheck(c);
      }
      setError(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e));
    } finally {
      setSaving(false);
    }
  };

  const cs = check?.checksum;
  const csGood = cs && (cs.overall === "Corrected" || cs.overall === "Valid");

  return (
    <Dialog open={open} onClose={onClose} className="max-w-2xl" title={<span className="flex items-center gap-2"><FilePlus2 className="size-4" />{t("editor.save.title")}</span>}
      footer={result ? <Button onClick={onClose}>{t("common.close")}</Button> : <>
        <Button variant="ghost" onClick={onClose}>{t("editor.save.cancel")}</Button>
        <Button variant="primary" disabled={!check?.canSave || !acksDone || !nameOk || checking || saving} onClick={save}>
          {saving ? <Spinner /> : <FilePlus2 className="size-3.5" />}{t("editor.save.saveAsNew")}
        </Button>
      </>}>
      <div className="max-h-[65vh] space-y-3 overflow-y-auto text-xs">
        <div className="rounded-md border border-calc/30 bg-calc/8 px-3 py-2 text-fg">{t("editor.save.neverOverwrite", { name: file.name })}</div>

        {result ? (
          <div className="space-y-3">
            <div className={cn("flex items-center gap-2 font-semibold", result.verified ? "text-ok" : "text-danger")}>
              {result.verified ? <CheckCircle2 className="size-4" /> : <XCircle className="size-4" />}
              {result.verified ? t("editor.save.verified") : t("editor.save.notVerified")}
            </div>
            <div>
              <SectionTitle className="mb-1">{t("editor.save.verification")}</SectionTitle>
              <ul className="space-y-0.5">{result.verification.map((l, i) => <li key={i} className="num text-fg-muted">· {l}</li>)}</ul>
            </div>
            <div className="rounded-md border border-border p-2">
              <div className="font-medium">{result.file.name}</div>
              <div className="num text-[11px] text-fg-subtle">{result.file.size.toLocaleString()} B · SHA-256 {result.file.sha256.slice(0, 16)}…</div>
              <div className="mt-2 flex gap-2">
                <a href={editingApi.contentUrl(projectId, result.file.id)} download={result.file.name}
                  className="inline-flex h-7 items-center gap-1.5 rounded-md border border-border bg-panel-2 px-2.5 text-xs font-medium hover:bg-border">
                  <Download className="size-3.5" />{t("editor.save.download")}
                </a>
                <Button onClick={() => { onOpenFile(result.file.id); onClose(); }}>{t("editor.save.openNew")}</Button>
              </div>
            </div>
            {!(result.check.checksum.overall === "Corrected" || result.check.checksum.overall === "Valid") && (
              <div className="rounded-md border border-danger/40 bg-danger/10 px-3 py-2 font-semibold text-danger">{t("editor.save.doNotFlash")}</div>
            )}
          </div>
        ) : (
          <>
            <label className="block">
              <span className="mb-1 block text-fg-subtle">{t("editor.save.name")}</span>
              <Input value={name} onChange={(e) => setName(e.target.value)} onBlur={() => runCheck(name, ackChecksum, ackCode)} placeholder={check?.suggestedName} className="num" />
              {!nameOk && name && <span className="text-[11px] text-danger">{t("editor.save.badName")}</span>}
            </label>

            <div>
              <SectionTitle className="mb-1 flex items-center gap-2">{t("editor.save.checks")}{checking && <Spinner className="size-3" />}</SectionTitle>
              {check ? (
                <ul className="space-y-1">
                  {check.checks.map((c) => (
                    <li key={c.id} className="flex items-start gap-2">
                      {statusIcon[c.status]}
                      <span className="w-28 shrink-0 font-medium">{t.tx(`editor.save.check.${c.id}`, c.id)}</span>
                      <span className="flex-1 text-fg-muted">{c.message}</span>
                      <Badge tone={c.status === "Pass" ? "ok" : c.status === "Warn" ? "warn" : "danger"}>{t.tx(`editor.save.status.${c.status}`, c.status)}</Badge>
                    </li>
                  ))}
                </ul>
              ) : !error && <Spinner />}
            </div>

            {cs && (
              <div className={cn("rounded-md border px-3 py-2", csGood ? "border-ok/40 bg-ok/8" : "border-danger/40 bg-danger/10")}>
                <div className={cn("flex items-center gap-2 font-semibold", csGood ? "text-ok" : "text-danger")}>
                  {csGood ? <CheckCircle2 className="size-4" /> : <XCircle className="size-4" />}
                  {t("editor.save.checksum", { status: t.tx(`checksum.${cs.overall}`, cs.overall) })}
                </div>
                {!csGood && <div className="mt-1 font-semibold text-danger">{t("editor.save.doNotFlash")}</div>}
                {csGood && <div className="mt-1 text-fg-muted">{t("editor.save.checksumGoodNote")}</div>}
                <ul className="mt-1.5 space-y-0.5">
                  {cs.blocks.map((b, i) => (
                    <li key={i} className="flex items-center gap-2 text-[11px]">
                      <span className={cn("size-1.5 shrink-0 rounded-full", b.status === "Corrected" || b.status === "Valid" ? "bg-ok" : "bg-danger")} />
                      <span className="font-medium">{b.name}</span>
                      <span className="num truncate text-fg-subtle">{b.algorithm}</span>
                      <span className="ml-auto shrink-0">{t.tx(`checksum.${b.status}`, b.status)}</span>
                    </li>
                  ))}
                </ul>
                {cs.note && <div className="mt-1 text-[11px] text-fg-subtle">{cs.note}</div>}
              </div>
            )}

            {needs.length > 0 && (
              <div className="space-y-1.5 rounded-md border border-warn/40 bg-warn/8 px-3 py-2">
                <SectionTitle>{t("editor.save.acknowledge")}</SectionTitle>
                {needs.includes("checksum") && (
                  <label className="flex items-start gap-2"><input type="checkbox" className="mt-0.5" checked={ackChecksum} onChange={(e) => setAck("checksum", e.target.checked)} />{t("editor.save.ackChecksum")}</label>
                )}
                {needs.includes("code") && (
                  <label className="flex items-start gap-2"><input type="checkbox" className="mt-0.5" checked={ackCode} onChange={(e) => setAck("code", e.target.checked)} />{t("editor.save.ackCode")}</label>
                )}
              </div>
            )}

            {check && !check.canSave && acksDone && !checking && <div className="text-danger">{t("editor.save.blocked")}</div>}
          </>
        )}
        {error && <div className="rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-danger">{error}</div>}
      </div>
    </Dialog>
  );
}
