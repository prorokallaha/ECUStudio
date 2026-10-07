"use client";
import { useState } from "react";
import Link from "next/link";
import { Bot, Check, PencilLine, X } from "lucide-react";
import { toast } from "sonner";
import type { AnalysisReport, MapCandidate, MapHypothesisResult, MapRole } from "@/types/domain";
import { api, ApiError } from "@/services/api";
import { useInfo, useRunAnalysis } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useHistory } from "@/stores/history";
import { AIBadge, Badge, Button, Card, CardHeader, ConfidenceBadge, Dialog, Input, SectionTitle, Select, SourceBadge } from "@/components/ui";
import { EvidenceList } from "@/components/ai/answer-card";
import { hex } from "@/lib/format";
import { useT } from "@/i18n";

const ROLES: MapRole[] = ["DriverWish", "TorqueLimiter", "GearTorqueLimiter", "TorqueToIq", "SmokeLimiter", "BoostTarget", "BoostLimiter", "Svbl", "VntDuty", "Soi", "Duration", "RailPressure", "LambdaTarget", "EgtProtection", "TemperatureProtection", "RpmLimiter", "GearboxTorqueMonitor"];

/** Unknown map workflow: signature/AI hypotheses stay candidates until a person confirms or rejects them. */
export function CandidatePanel({ r, c }: { r: AnalysisReport; c: MapCandidate }) {
  const t = useT();
  const role_ = (r?: string | null) => (r ? t.tx(`role.${r}`, r) : "");
  const { href, projectId } = useWorkspace();
  const info = useInfo();
  const run = useRunAnalysis();
  const history = useHistory();
  const [ai, setAi] = useState<MapHypothesisResult[] | null>(null);
  const [aiBusy, setAiBusy] = useState(false);
  const [edit, setEdit] = useState(false);
  const [role, setRole] = useState<MapRole>((c.best?.role as MapRole) ?? "Unknown");
  const [note, setNote] = useState("");

  const decide = async (decision: "confirm" | "reject", chosen?: MapRole, n?: string) => {
    try {
      await history.execute({
        label: decision === "confirm" ? t("maps.candidate.historyConfirm", { id: c.id }) : t("maps.candidate.historyReject", { id: c.id }),
        run: async () => {
          await api.analyses.decide(r.id, c.id, { decision, role: chosen, note: n });
          if (projectId) run.mutate({ projectId });
        },
        undo: async () => { toast(t("maps.candidate.appendOnly")); },
      });
      toast.success(decision === "confirm" ? t("maps.candidate.confirmedToast", { role: role_(chosen) }) : t("maps.candidate.rejectedToast"));
    } catch (e) { toast.error(e instanceof ApiError ? e.message : String(e)); }
  };

  const askAI = async () => {
    setAiBusy(true);
    try { setAi(await api.analyses.hypotheses(r.id, c.id)); } catch (e) { toast.error(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e)); } finally { setAiBusy(false); }
  };

  return (
    <div className="space-y-3 p-4">
      <div className="flex items-center gap-2">
        <div className="text-sm font-semibold">{t("maps.candidate.unknownMap")} <span className="num">{hex(c.address)}</span></div>
        <Badge tone={c.status === "Confirmed" ? "ok" : c.status === "Rejected" ? "danger" : "ai"}>{t.tx(`candidateStatus.${c.status}`, c.status)}</Badge>
        <Link href={href("binary", { offset: c.headerAddress })} className="text-[11px] text-calc hover:underline">{t("maps.candidate.openInHex")}</Link>
        <div className="ml-auto flex gap-1.5">
          <Button variant="primary" size="sm" disabled={!c.best || c.best.role === "Unknown"} onClick={() => decide("confirm", c.best!.role)}><Check className="size-3.5" />{t("maps.candidate.confirm", { role: role_(c.best?.role) })}</Button>
          <Button variant="danger" size="sm" onClick={() => decide("reject")}><X className="size-3.5" />{t("maps.candidate.reject")}</Button>
          <Button size="sm" onClick={() => setEdit(true)}><PencilLine className="size-3.5" />{t("maps.candidate.editDefinition")}</Button>
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3 text-xs xl:grid-cols-4">
        <Fact k={t("maps.candidate.header")} v={hex(c.headerAddress)} /><Fact k={t("maps.candidate.data")} v={`${hex(c.address)} · ${c.rows}×${c.cols} ${c.dataType}`} />
        <Fact k={t("maps.candidate.rawRange")} v={`${c.rawMin} … ${c.rawMax}`} /><Fact k={t("maps.candidate.endian")} v={t.tx(`endian.${c.endian}`, c.endian)} />
      </div>
      <Card>
        <CardHeader title={t("maps.candidate.axesRaw")} />
        <div className="space-y-1 p-3 font-mono text-[11px]">
          <div><span className="text-fg-subtle">X ({c.xAxisGuesses?.[0]?.quantity ? t.tx(`axisQuantity.${c.xAxisGuesses[0].quantity}`, c.xAxisGuesses[0].quantity) : "?"}): </span>{c.xAxisRaw.join(" ")}</div>
          <div><span className="text-fg-subtle">Y ({c.yAxisGuesses?.[0]?.quantity ? t.tx(`axisQuantity.${c.yAxisGuesses[0].quantity}`, c.yAxisGuesses[0].quantity) : "?"}): </span>{c.yAxisRaw.join(" ")}</div>
        </div>
      </Card>
      <Card>
        <CardHeader title={t("maps.candidate.sigHyp")} subtitle={t("maps.candidate.sigHypSubtitle")} />
        <table className="w-full text-xs">
          <tbody>
            {(c.hypotheses ?? []).map((h, i) => (
              <tr key={i} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5">
                <td className="font-medium">{role_(h.role)}</td>
                <td><ConfidenceBadge score={h.confidence} /></td>
                <td><SourceBadge source={h.source} /></td>
                <td className="text-fg-muted">{h.rationale}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
      <Card>
        <CardHeader title={t("maps.candidate.aiHyp")} actions={<Button size="xs" variant="ai" disabled={!info.data?.aiConfigured || aiBusy} onClick={askAI}><Bot className="size-3" />{aiBusy ? t("maps.candidate.asking") : t("maps.candidate.askAI")}</Button>} />
        <div className="space-y-2 p-3">
          {!info.data?.aiConfigured && <div className="text-xs text-fg-muted">{t("maps.candidate.aiDisabled")}</div>}
          {ai?.map((h, i) => (
            <div key={i} className="rounded-md border border-ai/25 bg-ai/5 p-2 text-xs">
              <div className="flex items-center gap-1.5"><AIBadge /><span className="font-semibold">{role_(h.role)}</span><ConfidenceBadge score={h.confidence} /><Button size="xs" variant="ghost" className="ml-auto" onClick={() => decide("confirm", h.role as MapRole, "accepted AI hypothesis")}>{t("maps.candidate.confirmBtn")}</Button></div>
              <div className="mt-1 text-fg-muted">{h.rationale}</div>
              <div className="mt-1"><EvidenceList evidence={h.evidence} /></div>
            </div>
          ))}
          {ai && ai.length === 0 && <div className="text-xs text-fg-muted">{t("maps.candidate.aiNone")}</div>}
        </div>
      </Card>
      <Dialog open={edit} onClose={() => setEdit(false)} title={t("maps.candidate.editTitle", { addr: hex(c.address) })} footer={<>
        <Button variant="ghost" onClick={() => setEdit(false)}>{t("common.cancel")}</Button>
        <Button variant="primary" disabled={role === "Unknown"} onClick={() => { setEdit(false); decide("confirm", role, note || undefined); }}>{t("maps.candidate.saveConfirmed")}</Button>
      </>}>
        <div className="space-y-3 text-xs">
          <label className="block space-y-1"><SectionTitle>{t("maps.candidate.role")}</SectionTitle>
            <Select className="w-full" value={role} onChange={(e) => setRole(e.target.value as MapRole)}>
              <option value="Unknown" disabled>{t("maps.candidate.selectRole")}</option>
              {ROLES.map((x) => <option key={x} value={x}>{role_(x)}</option>)}
            </Select>
          </label>
          <label className="block space-y-1"><SectionTitle>{t("maps.candidate.noteLabel")}</SectionTitle><Input value={note} onChange={(e) => setNote(e.target.value)} placeholder={t("maps.candidate.notePlaceholder")} /></label>
          <div className="text-[11px] text-fg-subtle">{t("maps.candidate.scalingHint")}</div>
        </div>
      </Dialog>
    </div>
  );
}

function Fact({ k, v }: { k: string; v: string }) {
  return <div className="rounded-md border border-border bg-panel px-2.5 py-1.5"><div className="text-[10px] uppercase tracking-wide text-fg-subtle">{k}</div><div className="num">{v}</div></div>;
}
