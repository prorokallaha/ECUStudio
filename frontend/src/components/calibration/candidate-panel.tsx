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

const ROLES: MapRole[] = ["DriverWish", "TorqueLimiter", "GearTorqueLimiter", "TorqueToIq", "SmokeLimiter", "BoostTarget", "BoostLimiter", "Svbl", "VntDuty", "Soi", "Duration", "RailPressure", "LambdaTarget", "EgtProtection", "TemperatureProtection", "RpmLimiter", "GearboxTorqueMonitor"];

/** Unknown map workflow: signature/AI hypotheses stay candidates until a person confirms or rejects them. */
export function CandidatePanel({ r, c }: { r: AnalysisReport; c: MapCandidate }) {
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
        label: `${decision} ${c.id}`,
        run: async () => {
          await api.analyses.decide(r.id, c.id, { decision, role: chosen, note: n });
          if (projectId) run.mutate({ projectId });
        },
        undo: async () => { toast("Decision history is append-only: record the opposite decision to change it."); },
      });
      toast.success(decision === "confirm" ? `Confirmed as ${chosen} — recomputing` : "Rejected — recomputing");
    } catch (e) { toast.error(e instanceof ApiError ? e.message : String(e)); }
  };

  const askAI = async () => {
    setAiBusy(true);
    try { setAi(await api.analyses.hypotheses(r.id, c.id)); } catch (e) { toast.error(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e)); } finally { setAiBusy(false); }
  };

  return (
    <div className="space-y-3 p-4">
      <div className="flex items-center gap-2">
        <div className="text-sm font-semibold">Unknown map <span className="num">{hex(c.address)}</span></div>
        <Badge tone={c.status === "Confirmed" ? "ok" : c.status === "Rejected" ? "danger" : "ai"}>{c.status}</Badge>
        <Link href={href("binary", { offset: c.headerAddress })} className="text-[11px] text-calc hover:underline">open in hex</Link>
        <div className="ml-auto flex gap-1.5">
          <Button variant="primary" size="sm" disabled={!c.best || c.best.role === "Unknown"} onClick={() => decide("confirm", c.best!.role)}><Check className="size-3.5" />CONFIRM {c.best?.role}</Button>
          <Button variant="danger" size="sm" onClick={() => decide("reject")}><X className="size-3.5" />REJECT</Button>
          <Button size="sm" onClick={() => setEdit(true)}><PencilLine className="size-3.5" />EDIT DEFINITION</Button>
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3 text-xs xl:grid-cols-4">
        <Fact k="Header" v={hex(c.headerAddress)} /><Fact k="Data" v={`${hex(c.address)} · ${c.rows}×${c.cols} ${c.dataType}`} />
        <Fact k="Raw range" v={`${c.rawMin} … ${c.rawMax}`} /><Fact k="Endian" v={c.endian} />
      </div>
      <Card>
        <CardHeader title="Axes (raw)" />
        <div className="space-y-1 p-3 font-mono text-[11px]">
          <div><span className="text-fg-subtle">X ({c.xAxisGuesses?.[0]?.quantity ?? "?"}): </span>{c.xAxisRaw.join(" ")}</div>
          <div><span className="text-fg-subtle">Y ({c.yAxisGuesses?.[0]?.quantity ?? "?"}): </span>{c.yAxisRaw.join(" ")}</div>
        </div>
      </Card>
      <Card>
        <CardHeader title="Signature hypotheses" subtitle="deterministic classifier — 25 % mass is always reserved for “unknown”" />
        <table className="w-full text-xs">
          <tbody>
            {(c.hypotheses ?? []).map((h, i) => (
              <tr key={i} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5">
                <td className="font-medium">{h.role}</td>
                <td><ConfidenceBadge score={h.confidence} /></td>
                <td><SourceBadge source={h.source} /></td>
                <td className="text-fg-muted">{h.rationale}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
      <Card>
        <CardHeader title="AI hypotheses" actions={<Button size="xs" variant="ai" disabled={!info.data?.aiConfigured || aiBusy} onClick={askAI}><Bot className="size-3" />{aiBusy ? "Asking…" : "Ask AI"}</Button>} />
        <div className="space-y-2 p-3">
          {!info.data?.aiConfigured && <div className="text-xs text-fg-muted">AI disabled (no API key). Signature hypotheses above still work.</div>}
          {ai?.map((h, i) => (
            <div key={i} className="rounded-md border border-ai/25 bg-ai/5 p-2 text-xs">
              <div className="flex items-center gap-1.5"><AIBadge /><span className="font-semibold">{h.role}</span><ConfidenceBadge score={h.confidence} /><Button size="xs" variant="ghost" className="ml-auto" onClick={() => decide("confirm", h.role as MapRole, "accepted AI hypothesis")}>Confirm</Button></div>
              <div className="mt-1 text-fg-muted">{h.rationale}</div>
              <div className="mt-1"><EvidenceList evidence={h.evidence} /></div>
            </div>
          ))}
          {ai && ai.length === 0 && <div className="text-xs text-fg-muted">AI returned no supported hypothesis.</div>}
        </div>
      </Card>
      <Dialog open={edit} onClose={() => setEdit(false)} title={`Edit definition · ${hex(c.address)}`} footer={<>
        <Button variant="ghost" onClick={() => setEdit(false)}>Cancel</Button>
        <Button variant="primary" disabled={role === "Unknown"} onClick={() => { setEdit(false); decide("confirm", role, note || undefined); }}>Save as confirmed</Button>
      </>}>
        <div className="space-y-3 text-xs">
          <label className="block space-y-1"><SectionTitle>Role</SectionTitle>
            <Select className="w-full" value={role} onChange={(e) => setRole(e.target.value as MapRole)}>
              <option value="Unknown" disabled>Select role…</option>
              {ROLES.map((x) => <option key={x} value={x}>{x}</option>)}
            </Select>
          </label>
          <label className="block space-y-1"><SectionTitle>Note (scaling, source, reasoning)</SectionTitle><Input value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. matches DAMOS AccPed_trqEngHiGear" /></label>
          <div className="text-[11px] text-fg-subtle">Scaling comes from the plugin's role defaults. For exact factors, import an XDF/JSON definition.</div>
        </div>
      </Dialog>
    </div>
  );
}

function Fact({ k, v }: { k: string; v: string }) {
  return <div className="rounded-md border border-border bg-panel px-2.5 py-1.5"><div className="text-[10px] uppercase tracking-wide text-fg-subtle">{k}</div><div className="num">{v}</div></div>;
}
