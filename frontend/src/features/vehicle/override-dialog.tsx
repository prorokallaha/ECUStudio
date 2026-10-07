"use client";
import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import type { ComponentKind, ComponentSpec } from "@/types/domain";
import { api } from "@/services/api";
import { Button, Dialog, Input, SectionTitle, Select } from "@/components/ui";
import { fmtParam } from "@/lib/format";

export function OverrideDialog({ kind, current, open, onClose, onApply }: {
  kind: ComponentKind | null; current?: ComponentSpec; open: boolean; onClose: () => void;
  onApply: (catalogId: string, note: string) => Promise<void>;
}) {
  const catalog = useQuery({ queryKey: ["components", kind], queryFn: () => api.components(kind!), enabled: !!kind && open });
  const [choice, setChoice] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (open) { setChoice(current?.id ?? ""); setNote(""); } }, [open, current]);
  const selected = catalog.data?.find((c) => c.id === choice);
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={`Override ${kind ?? ""}`}
      footer={<>
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!choice || choice === current?.id || busy} onClick={async () => { setBusy(true); try { await onApply(choice, note); onClose(); } finally { setBusy(false); } }}>
          Apply &amp; recompute
        </Button>
      </>}
    >
      <div className="space-y-3 text-xs">
        <div className="text-fg-muted">Current: <span className="text-fg">{current?.name ?? "—"}</span></div>
        <label className="block space-y-1">
          <SectionTitle>Installed component</SectionTitle>
          <Select className="w-full" value={choice} onChange={(e) => setChoice(e.target.value)}>
            <option value="" disabled>Select…</option>
            {catalog.data?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </Select>
        </label>
        {selected && (
          <div className="rounded-md border border-border bg-panel p-2">
            {Object.entries(selected.parameters ?? {}).slice(0, 8).map(([k, p]) => (
              <div key={k} className="flex justify-between py-0.5"><span className="text-fg-subtle">{k}</span><span className={fmtParam(p) === "UNKNOWN" ? "text-unknown" : "num"}>{fmtParam(p)}</span></div>
            ))}
            {selected.note && <div className="mt-1 text-[11px] text-fg-muted">{selected.note}</div>}
          </div>
        )}
        <label className="block space-y-1"><SectionTitle>Note</SectionTitle><Input value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. receipt / part number checked" /></label>
        <div className="text-[11px] text-fg-subtle">The override is marked “User entered”. Simulation and risk are recomputed with the new component limits; unknown limits stay UNKNOWN.</div>
      </div>
    </Dialog>
  );
}
