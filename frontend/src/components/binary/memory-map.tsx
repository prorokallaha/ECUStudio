"use client";
import { useState } from "react";
import type { AnalysisReport } from "@/types/domain";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

const SECTION_COLORS: Record<string, string> = {
  Boot: "bg-fg-subtle/40", Code: "bg-calc/35", Calibration: "bg-ok/30", Data: "bg-attn/30", Empty: "bg-panel-2", Unknown: "bg-unknown/30",
};

/** Interactive flash layout: sections, identified maps, unknown candidates and changed regions. Click to open in hex. */
export function MemoryMap({ r, onPick }: { r: AnalysisReport; onPick: (offset: number) => void }) {
  const t = useT();
  const size = r.ecu.flashSize || 1;
  const [hover, setHover] = useState<string | null>(null);
  const X = (a: number) => `${(a / size) * 100}%`;
  const W = (len: number) => `max(${(len / size) * 100}%, 2px)`;
  return (
    <div className="space-y-2">
      <div className="relative h-10 overflow-hidden rounded border border-border bg-bg">
        {r.ecu.sections.map((s) => (
          <button key={s.start} onClick={() => onPick(s.start)} onMouseEnter={() => setHover(t("ecu.tipSection", { name: s.name, kind: t.tx(`sectionKind.${s.kind}`, s.kind), from: hex(s.start), to: hex(s.end), source: t.tx(`source.${s.source}`, s.source), conf: s.confidence.toFixed(2) }))} onMouseLeave={() => setHover(null)}
            className={cn("absolute inset-y-0 border-r border-bg/60 hover:brightness-150", SECTION_COLORS[s.kind] ?? "bg-unknown/30")} style={{ left: X(s.start), width: W(s.end - s.start) }} />
        ))}
      </div>
      <Lane label={t("ecu.laneMaps")} items={r.maps.map((m) => ({ key: m.id, start: m.address, len: m.rows * m.cols * 2, cls: m.modified ? "bg-calc" : "bg-ok", tip: `${m.name} ${hex(m.address)}${m.modified ? t("ecu.tipModified") : ""}` }))} X={X} W={W} onPick={onPick} setHover={setHover} />
      <Lane label={t("ecu.laneUnknown")} items={r.candidates.map((c) => ({ key: c.id, start: c.address ?? 0, len: (c.rows ?? 1) * (c.cols ?? 1) * 2, cls: "bg-ai", tip: t("ecu.tipCandidate", { id: c.id, addr: hex(c.address ?? 0), rows: c.rows, cols: c.cols }) }))} X={X} W={W} onPick={onPick} setHover={setHover} />
      <Lane label={t("ecu.laneChanged")} items={(r.unmappedChanges ?? []).map((u) => ({ key: `u${u.start}`, start: u.start, len: u.length, cls: u.section === "Code" ? "bg-danger" : "bg-warn", tip: t("ecu.tipUnmapped", { addr: hex(u.start), len: u.length, section: t.tx(`sectionKind.${u.section}`, u.section) }) }))
        .concat(r.maps.filter((m) => m.modified).map((m) => ({ key: `m${m.id}`, start: m.address, len: m.rows * m.cols * 2, cls: "bg-calc", tip: t("ecu.tipMapModified", { name: m.name, pct: m.modifiedPct.toFixed(0) }) })))} X={X} W={W} onPick={onPick} setHover={setHover} />
      <div className="flex items-center justify-between text-[10px] text-fg-subtle num">
        <span>{hex(0)}</span><span className="text-fg-muted">{hover ?? t("ecu.hoverHint")}</span><span>{hex(size)}</span>
      </div>
      <div className="flex flex-wrap gap-3 text-[10px] text-fg-muted">
        {Object.entries(SECTION_COLORS).map(([k, c]) => <span key={k} className="flex items-center gap-1"><span className={cn("size-2 rounded-sm", c)} />{t.tx(`sectionKind.${k}`, k)}</span>)}
        <span className="flex items-center gap-1"><span className="size-2 rounded-sm bg-ai" />{t("ecu.legendAi")}</span>
        <span className="flex items-center gap-1"><span className="size-2 rounded-sm bg-danger" />{t("ecu.legendCode")}</span>
      </div>
    </div>
  );
}

function Lane({ label, items, X, W, onPick, setHover }: { label: string; items: { key: string; start: number; len: number; cls: string; tip: string }[]; X: (a: number) => string; W: (l: number) => string; onPick: (o: number) => void; setHover: (s: string | null) => void }) {
  return (
    <div className="flex items-center gap-2">
      <span className="w-16 shrink-0 text-[10px] uppercase tracking-wide text-fg-subtle">{label}</span>
      <div className="relative h-3 flex-1 rounded bg-panel-2/60">
        {items.map((i) => (
          <button key={i.key} onClick={() => onPick(i.start)} onMouseEnter={() => setHover(i.tip)} onMouseLeave={() => setHover(null)} className={cn("absolute inset-y-0 rounded-sm hover:ring-1 hover:ring-fg", i.cls)} style={{ left: X(i.start), width: W(i.len) }} />
        ))}
      </div>
    </div>
  );
}
