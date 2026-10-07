"use client";
import { useState } from "react";
import type { AnalysisReport } from "@/types/domain";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";

const SECTION_COLORS: Record<string, string> = {
  Boot: "bg-fg-subtle/40", Code: "bg-calc/35", Calibration: "bg-ok/30", Data: "bg-attn/30", Empty: "bg-panel-2", Unknown: "bg-unknown/30",
};

/** Interactive flash layout: sections, identified maps, unknown candidates and changed regions. Click to open in hex. */
export function MemoryMap({ r, onPick }: { r: AnalysisReport; onPick: (offset: number) => void }) {
  const size = r.ecu.flashSize || 1;
  const [hover, setHover] = useState<string | null>(null);
  const X = (a: number) => `${(a / size) * 100}%`;
  const W = (len: number) => `max(${(len / size) * 100}%, 2px)`;
  return (
    <div className="space-y-2">
      <div className="relative h-10 overflow-hidden rounded border border-border bg-bg">
        {r.ecu.sections.map((s) => (
          <button key={s.start} onClick={() => onPick(s.start)} onMouseEnter={() => setHover(`${s.name} · ${s.kind} · ${hex(s.start)}–${hex(s.end)} (${s.source}, conf ${s.confidence.toFixed(2)})`)} onMouseLeave={() => setHover(null)}
            className={cn("absolute inset-y-0 border-r border-bg/60 hover:brightness-150", SECTION_COLORS[s.kind] ?? "bg-unknown/30")} style={{ left: X(s.start), width: W(s.end - s.start) }} />
        ))}
      </div>
      <Lane label="Maps" items={r.maps.map((m) => ({ key: m.id, start: m.address, len: m.rows * m.cols * 2, cls: m.modified ? "bg-calc" : "bg-ok", tip: `${m.name} ${hex(m.address)}${m.modified ? " · modified" : ""}` }))} X={X} W={W} onPick={onPick} setHover={setHover} />
      <Lane label="Unknown" items={r.candidates.map((c) => ({ key: c.id, start: c.address ?? 0, len: (c.rows ?? 1) * (c.cols ?? 1) * 2, cls: "bg-ai", tip: `Candidate ${c.id} ${hex(c.address ?? 0)} ${c.rows}×${c.cols}` }))} X={X} W={W} onPick={onPick} setHover={setHover} />
      <Lane label="Changed" items={(r.unmappedChanges ?? []).map((u) => ({ key: `u${u.start}`, start: u.start, len: u.length, cls: u.section === "Code" ? "bg-danger" : "bg-warn", tip: `Unmapped change ${hex(u.start)} (${u.length} B) in ${u.section}` }))
        .concat(r.maps.filter((m) => m.modified).map((m) => ({ key: `m${m.id}`, start: m.address, len: m.rows * m.cols * 2, cls: "bg-calc", tip: `${m.name} modified (${m.modifiedPct.toFixed(0)}% cells)` })))} X={X} W={W} onPick={onPick} setHover={setHover} />
      <div className="flex items-center justify-between text-[10px] text-fg-subtle num">
        <span>{hex(0)}</span><span className="text-fg-muted">{hover ?? "hover for details · click to open in Binary Viewer"}</span><span>{hex(size)}</span>
      </div>
      <div className="flex flex-wrap gap-3 text-[10px] text-fg-muted">
        {Object.entries(SECTION_COLORS).map(([k, c]) => <span key={k} className="flex items-center gap-1"><span className={cn("size-2 rounded-sm", c)} />{k}</span>)}
        <span className="flex items-center gap-1"><span className="size-2 rounded-sm bg-ai" />AI/unknown candidate</span>
        <span className="flex items-center gap-1"><span className="size-2 rounded-sm bg-danger" />change in code</span>
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
