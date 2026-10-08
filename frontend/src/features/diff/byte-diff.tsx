"use client";
import { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowLeftRight, ChevronDown, ChevronUp, FileWarning } from "lucide-react";
import type { AnalysisReport, ProjectFile } from "@/types/domain";
import type { DiffRange } from "@/types/editing";
import { PageSkeleton } from "@/components/layout/page";
import { Badge, Button, EmptyState, SectionTitle, Select, Spinner } from "@/components/ui";
import { DisplayToolbar } from "@/components/binary/display-toolbar";
import { FoldRow } from "@/components/binary/fold-row";
import { PAGE, useHexPages } from "@/components/binary/use-hex-pages";
import {
  ROW, analysisRegions, cellText, cellWidth, decode, diffBuffers, findPaddingRuns, fmtFloat, rangeIndex, regionsFor, useRowLayout, wordBytes,
  type AnalysisRegion, type RowLayout,
} from "@/components/binary/row-model";
import { useProject, useReport } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useEditorPrefs } from "@/stores/editor";
import { useFileContent } from "@/features/binary/use-edits";
import { ApiError } from "@/services/api";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

const regionTint: Record<string, string> = { map: "bg-ok/10", axis: "bg-ok/5", candidate: "bg-ai/12", section: "" };
const LIST_CAP = 2000;

/**
 * Byte-level comparison of ANY two project files: A | B | Δ in one virtualised, scroll-synchronised view
 * (one scroll container ⇒ the three panes always show the same offsets). Both files are downloaded once
 * (≤ 16 MB, typed arrays) and diffed client-side; only visible rows are rendered.
 */
export function ByteDiff() {
  const t = useT();
  const { projectId, analysisId, params, setParam, router } = useWorkspace();
  const project = useProject(projectId);
  const report = useReport();
  const r = analysisId ? report.data : undefined;
  const files = useMemo(() => project.data?.files ?? [], [project.data]);

  const defA = files.find((f) => f.sha256 === r?.stockSha256) ?? files.find((f) => f.role === "Stock") ?? files[0];
  const defB = files.find((f) => f.sha256 === r?.modifiedSha256) ?? files.find((f) => f.role === "Modified" && f.id !== defA?.id) ?? files.find((f) => f.id !== defA?.id) ?? defA;
  const fa = files.find((f) => f.id === params.get("fa")) ?? defA;
  const fb = files.find((f) => f.id === params.get("fb")) ?? defB;

  if (!projectId || (!project.isLoading && !files.length)) return <EmptyState icon={<FileWarning className="size-8" />} title={t("layout.noBinary")}>{t("layout.noBinaryHint")}</EmptyState>;
  if (project.isLoading || !fa || !fb) return <PageSkeleton />;
  return <DiffBody key={`${fa.id}:${fb.id}`} projectId={projectId} files={files} fa={fa} fb={fb} report={r}
    onPick={(which, id) => setParam(which, id)} onSwap={() => {
      const q = new URLSearchParams(params.toString());
      q.set("fa", fb.id); q.set("fb", fa.id);
      router.replace(`${window.location.pathname}?${q.toString()}`, { scroll: false });
    }} />;
}

function DiffBody({ projectId, files, fa, fb, report, onPick, onSwap }: {
  projectId: string; files: ProjectFile[]; fa: ProjectFile; fb: ProjectFile; report?: AnalysisReport;
  onPick: (which: "fa" | "fb", id: string) => void; onSwap: () => void;
}) {
  const t = useT();
  const prefs = useEditorPrefs();
  const ca = useFileContent(projectId, fa.id);
  const cb = useFileContent(projectId, fb.id);
  const a = ca.data, b = cb.data;

  const diff = useMemo(() => (a && b ? diffBuffers(a, b) : null), [a, b]);
  const size = diff?.size ?? Math.max(fa.size, fb.size);
  const idx = useMemo(() => rangeIndex(diff?.ranges ?? []), [diff]);
  const runs = useMemo(() => (a && b && prefs.hidePadding ? findPaddingRuns(a, Math.min(a.length, b.length), { b }) : []), [a, b, prefs.hidePadding]);
  const { layout, toggle, reveal } = useRowLayout(size, runs, prefs.hidePadding);

  // Map-aware labels come from the analysis and only apply to images with the analysed layout.
  const regionsOk = !!report && report.ecu.flashSize === size && (a?.length ?? 0) === (b?.length ?? 0);
  const isAnalysedPair = !!report && fa.sha256 === report.stockSha256 && fb.sha256 === report.modifiedSha256;
  const regions = useMemo(() => (regionsOk ? analysisRegions(report) : []), [regionsOk, report]);
  const loader = useHexPages(regionsOk ? report!.id : "", regionsOk ? size : 0);

  const [cursor, setCursor] = useState<number | null>(null);
  const [scrollTo, setScrollTo] = useState<{ offset: number; seq: number } | null>(null);
  const jump = (o: number) => { reveal(o); setCursor(o); setScrollTo({ offset: o, seq: Date.now() }); };
  const goNext = () => { const r = idx.next(cursor ?? -1); if (r) jump(r.start); };
  const goPrev = () => { const r = idx.prev(cursor ?? size); if (r) jump(r.start); };
  const curIdx = cursor === null ? -1 : idx.indexAt(cursor);
  const position = curIdx >= 0 && cursor !== null && cursor < (diff?.ranges[curIdx]?.end ?? -1) ? curIdx + 1 : null;

  const loadError = ca.error ?? cb.error;
  const pct = diff && diff.size ? (diff.changed / diff.size) * 100 : 0;
  const sameFile = fa.id === fb.id;

  return (
    <div className="flex h-full min-h-0 flex-col" onKeyDown={(e) => {
      const tg = e.target as HTMLElement;
      if (tg.tagName === "INPUT" || tg.tagName === "SELECT" || e.ctrlKey || e.metaKey) return;
      if (e.key === "n" || e.key === "F8") { e.preventDefault(); goNext(); }
      if (e.key === "p" || (e.key === "F8" && e.shiftKey)) { e.preventDefault(); goPrev(); }
    }}>
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-1.5">
        <FilePick label="A" value={fa.id} files={files} onChange={(id) => onPick("fa", id)} />
        <Button size="icon" variant="ghost" onClick={onSwap} title={t("editor.diff.swap")}><ArrowLeftRight className="size-3.5" /></Button>
        <FilePick label="B" value={fb.id} files={files} onChange={(id) => onPick("fb", id)} />
        <div className="mx-2 h-5 w-px bg-border" />
        <Button size="xs" onClick={goPrev} disabled={!diff?.ranges.length} title={t("editor.diff.prevTip")}><ChevronUp className="size-3" />{t("editor.diff.prev")}</Button>
        <Button size="xs" onClick={goNext} disabled={!diff?.ranges.length} title={t("editor.diff.nextTip")}><ChevronDown className="size-3" />{t("editor.diff.next")}</Button>
        {diff && <span className="num text-[11px] text-fg-muted">{position ? `${position} / ${diff.ranges.length}` : t("editor.diff.rangesN", { n: diff.ranges.length })}</span>}
        <div className="ml-auto"><DisplayToolbar ascii={false} /></div>
      </div>
      <div className="flex flex-wrap items-center gap-3 border-b border-border px-3 py-1 text-xs">
        {diff ? (
          <>
            <span className={cn("font-semibold", diff.changed ? "text-warn" : "text-ok")}>
              {diff.changed ? t("editor.diff.stats", { bytes: diff.changed.toLocaleString(), pct: pct < 0.01 && pct > 0 ? "<0.01" : pct.toFixed(2), ranges: diff.ranges.length }) : t("editor.diff.identical")}
            </span>
            {sameFile && <Badge tone="unknown">{t("editor.diff.sameFile")}</Badge>}
            {a && b && a.length !== b.length && <Badge tone="danger">{t("editor.diff.sizeMismatch", { a: a.length.toLocaleString(), b: b.length.toLocaleString() })}</Badge>}
            {isAnalysedPair && <Badge tone="ok">{t("editor.diff.analysedPair")}</Badge>}
            {!regionsOk && report && <span className="text-fg-subtle">{t("editor.diff.noLabels")}</span>}
          </>
        ) : !loadError && <span className="flex items-center gap-2 text-fg-muted"><Spinner />{t("editor.diff.loading")}</span>}
      </div>
      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1">
          {loadError ? (
            <EmptyState icon={<FileWarning className="size-8 text-danger" />} title={t("layout.loadFailed")}>{loadError instanceof ApiError ? `${loadError.code}: ${loadError.message}` : String(loadError)}</EmptyState>
          ) : !a || !b || !diff ? <PageSkeleton /> : (
            <DiffGrid a={a} b={b} size={size} layout={layout} onToggleFold={toggle} nameA={fa.name} nameB={fb.name} cursor={cursor} onCursor={setCursor} scrollTo={scrollTo}
              regionKind={regionsOk ? (o) => loader.regionsAt(o).find((g) => g.kind !== "section")?.kind : undefined}
              onVisible={regionsOk ? (from, to) => { const need: number[] = []; for (let p = Math.floor(from / PAGE); p <= Math.floor(Math.max(from, to - 1) / PAGE); p++) need.push(p); loader.ensure(need); } : undefined} />
          )}
        </div>
        <aside className="w-72 shrink-0 space-y-3 overflow-y-auto border-l border-border bg-bg-elev p-3 text-xs">
          {a && b && cursor !== null && <CursorInfo a={a} b={b} offset={cursor} regions={regions} />}
          <div>
            <SectionTitle className="mb-1">{t("editor.diff.regions", { n: diff?.ranges.length ?? 0 })}</SectionTitle>
            {diff && <RangeList ranges={diff.ranges} regions={regions} active={position ? position - 1 : -1} onJump={jump} />}
          </div>
        </aside>
      </div>
    </div>
  );
}

function FilePick({ label, value, files, onChange }: { label: string; value: string; files: ProjectFile[]; onChange: (id: string) => void }) {
  const t = useT();
  return (
    <label className="flex items-center gap-1 text-xs">
      <span className="font-semibold text-fg-muted">{label}</span>
      <Select value={value} onChange={(e) => onChange(e.target.value)} className="max-w-64">
        {files.map((f) => <option key={f.id} value={f.id}>{t.tx(`fileRole.${f.role}`, f.role)} · {f.label || f.name} ({f.name})</option>)}
      </Select>
    </label>
  );
}

function RangeList({ ranges, regions, active, onJump }: { ranges: DiffRange[]; regions: AnalysisRegion[]; active: number; onJump: (o: number) => void }) {
  const t = useT();
  if (!ranges.length) return <div className="text-[11px] text-fg-subtle">{t("editor.diff.none")}</div>;
  const shown = ranges.slice(0, LIST_CAP);
  return (
    <div className="space-y-0.5">
      {shown.map((r, i) => {
        const regs = regionsFor(regions, r.start, r.end);
        const map = regs.find((g) => g.kind === "map");
        const cand = regs.find((g) => g.kind === "candidate");
        const sec = regs.find((g) => g.kind === "section");
        return (
          <button key={r.start} onClick={() => onJump(r.start)} className={cn("flex w-full items-center gap-1.5 rounded px-1 py-0.5 text-left text-[11px] hover:bg-panel-2", i === active && "bg-calc/12")}>
            <span className="num text-warn">{hex(r.start)}</span>
            <span className="num shrink-0 text-fg-subtle">{t("editor.bytesN", { n: r.end - r.start })}</span>
            <span className="flex-1 truncate text-right text-fg-muted">
              {map ? map.label : cand ? t("editor.diff.candidate", { id: cand.label }) : sec ? t("binary.unmapped", { section: t.tx(`sectionKind.${sec.label}`, sec.label) }) : ""}
            </span>
          </button>
        );
      })}
      {ranges.length > LIST_CAP && <div className="pt-1 text-[11px] text-fg-subtle">{t("editor.diff.more", { n: (ranges.length - LIST_CAP).toLocaleString() })}</div>}
    </div>
  );
}

function CursorInfo({ a, b, offset, regions }: { a: Uint8Array; b: Uint8Array; offset: number; regions: AnalysisRegion[] }) {
  const t = useT();
  const prefs = useEditorPrefs();
  const get = (buf: Uint8Array) => [0, 1, 2, 3].map((k) => (offset + k < buf.length ? buf[offset + k] : null));
  const va = get(a), vb = get(b);
  const rows = ([["8", false], ["8", true], ["16", false], ["16", true], ["32", false], ["32", true], ["f32", false]] as const)
    .map(([w, s]) => [w === "f32" ? "f32" : `${s ? "i" : "u"}${w}`, decode(va, w, s, prefs.order), decode(vb, w, s, prefs.order)] as const);
  const fmt = (v: number | null, f: boolean) => (v === null ? "—" : f ? fmtFloat(v) : String(v));
  const regs = regionsFor(regions, offset, offset + 1);
  return (
    <div className="space-y-2">
      <div className="num text-sm font-semibold">{hex(offset)}</div>
      {regs.map((g, i) => (
        <div key={i} className="flex items-center gap-1.5">
          <Badge tone={g.kind === "candidate" ? "ai" : g.kind === "section" ? "unknown" : "ok"}>{t.tx(`binary.region.${g.kind}`, g.kind)}</Badge>
          <span className="truncate">{g.kind === "section" ? t.tx(`sectionKind.${g.label}`, g.label) : g.label}</span>
        </div>
      ))}
      <table className="w-full">
        <thead className="text-[10px] text-fg-subtle"><tr><th className="text-left font-medium">{t("binary.colType")}</th><th className="text-right font-medium">A</th><th className="text-right font-medium">B</th></tr></thead>
        <tbody>{rows.map(([ty, x, y]) => (
          <tr key={ty} className={cn(x !== y && "text-warn")}><td className="text-fg-subtle">{ty}</td><td className="num text-right">{fmt(x, ty === "f32")}</td><td className="num text-right">{fmt(y, ty === "f32")}</td></tr>
        ))}</tbody>
      </table>
      <div className="text-[10px] text-fg-subtle">{t("editor.values", { order: prefs.order === "Big" ? "HiLo (BE)" : "LoHi (LE)" })}</div>
    </div>
  );
}

function DiffGrid({ a, b, size, layout, onToggleFold, nameA, nameB, cursor, onCursor, scrollTo, regionKind, onVisible }: {
  a: Uint8Array; b: Uint8Array; size: number; layout: RowLayout; onToggleFold: (start: number) => void; nameA: string; nameB: string;
  cursor: number | null; onCursor: (o: number) => void; scrollTo: { offset: number; seq: number } | null;
  regionKind?: (o: number) => string | undefined; onVisible?: (from: number, to: number) => void;
}) {
  const t = useT();
  const prefs = useEditorPrefs();
  const parent = useRef<HTMLDivElement>(null);
  const v = useVirtualizer({ count: layout.count, getScrollElement: () => parent.current, estimateSize: () => 20, overscan: 16 });
  const items = v.getVirtualItems();
  const n = wordBytes(prefs.word);
  const words = ROW / n;
  const cw = cellWidth(prefs.word, prefs.base, prefs.signed);
  const dw = prefs.base === "hex" ? (n === 1 ? "w-[3.4ch]" : n === 2 ? "w-[5.6ch]" : "w-[10ch]") : cw;

  useEffect(() => {
    if (scrollTo) v.scrollToIndex(layout.indexOf(scrollTo.offset), { align: "center" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scrollTo?.seq]);

  const firstIdx = items[0]?.index ?? 0, lastIdx = items.at(-1)?.index ?? 0;
  useEffect(() => {
    if (!onVisible || !layout.count) return;
    const x = layout.rowAt(firstIdx), y = layout.rowAt(lastIdx);
    onVisible(x.kind === "data" ? x.offset : x.start, y.kind === "data" ? y.offset + ROW : y.end);
  }, [firstIdx, lastIdx, layout, onVisible]);

  const word = (buf: Uint8Array, off: number) => Array.from({ length: n }, (_, k) => (off + k < buf.length ? buf[off + k] : null));
  const gap = (w: number) => w % Math.max(1, 8 / n) === 0 && w > 0 && "ml-2";

  const pane = (buf: Uint8Array, rowOff: number, side: "a" | "b") => {
    const cells = [];
    for (let w = 0; w < words; w++) {
      const off = rowOff + w * n;
      if (off >= size) break;
      let changed = false;
      for (let k = 0; k < n && off + k < size; k++) if (a[off + k] !== b[off + k] || off + k >= a.length || off + k >= b.length) { changed = true; break; }
      const kind = regionKind?.(off);
      const isCur = cursor !== null && cursor >= off && cursor < off + n;
      cells.push(
        <span key={w} onMouseDown={() => onCursor(off)} className={cn(
          "inline-block cursor-pointer rounded-sm text-right tabular-nums", cw, kind && regionTint[kind],
          changed && (side === "a" ? "bg-stock/10 text-stock" : "bg-warn/12 font-semibold text-warn"),
          isCur && "outline outline-1 outline-calc", gap(w),
        )}>{cellText(word(buf, off), prefs.word, prefs.base, prefs.signed, prefs.order)}</span>,
      );
    }
    return cells;
  };

  const delta = (rowOff: number) => {
    const cells = [];
    for (let w = 0; w < words; w++) {
      const off = rowOff + w * n;
      if (off >= size) break;
      const x = decode(word(a, off), prefs.word, prefs.signed, prefs.order), y = decode(word(b, off), prefs.word, prefs.signed, prefs.order);
      let text = "·", cls = "text-fg-subtle/50";
      if (x === null || y === null) { text = x === y ? "·" : "∅"; cls = x === y ? cls : "text-danger"; }
      else if (x !== y || (prefs.word === "f32" && word(a, off).some((q, k) => q !== b[off + k]))) {
        const d = y - x;
        cls = d > 0 ? "text-calc font-semibold" : d < 0 ? "text-warn font-semibold" : "text-attn";
        text = prefs.word === "f32" ? (d > 0 ? "+" : "") + fmtFloat(d) : prefs.base === "hex" ? (d >= 0 ? "+" : "−") + Math.abs(d).toString(16).toUpperCase() : (d > 0 ? "+" : "") + d;
        if (d === 0) text = "≠";
      }
      cells.push(<span key={w} onMouseDown={() => onCursor(off)} className={cn("inline-block cursor-pointer text-right tabular-nums", dw, cls, gap(w))}>{text}</span>);
    }
    return cells;
  };

  const panes = [
    { key: "a", title: `A · ${nameA}`, width: cw, cls: "" },
    { key: "b", title: `B · ${nameB}`, width: cw, cls: "" },
    { key: "d", title: t("editor.diff.colDelta"), width: dw, cls: "border-l border-border pl-3" },
  ];

  return (
    <div ref={parent} className="h-full overflow-auto font-mono text-[12px] leading-5" tabIndex={0}>
      <div className="sticky top-0 z-10 flex items-end gap-4 border-b border-border bg-bg-elev px-3 py-1 text-[11px] text-fg-subtle">
        <span className="w-[8ch] shrink-0">{t("binary.offset")}</span>
        {panes.map((pn) => (
          <span key={pn.key} className={cn("inline-flex flex-none flex-col whitespace-pre", pn.cls)}>
            <span className="w-0 min-w-full truncate font-sans font-semibold text-fg-muted" title={pn.title}>{pn.title}</span>
            <span>
              {Array.from({ length: words }, (_, w) => (
                <span key={w} className={cn("inline-block text-right", pn.width, gap(w))}>{(w * n).toString(16).toUpperCase()}</span>
              ))}
            </span>
          </span>
        ))}
      </div>
      <div style={{ height: v.getTotalSize(), position: "relative" }}>
        {items.map((it) => {
          const row = layout.rowAt(it.index);
          if (row.kind !== "data") {
            return (
              <div key={it.key} className="absolute left-0 flex w-full items-center px-3 py-px" style={{ top: it.start, height: it.size }}>
                <FoldRow start={row.start} end={row.end} value={row.value} expanded={row.kind === "unfold"} onToggle={() => onToggleFold(row.start)} />
              </div>
            );
          }
          const off = row.offset;
          let rowChanged = false;
          for (let i = off; i < off + ROW && i < size; i++) if (a[i] !== b[i] || i >= a.length || i >= b.length) { rowChanged = true; break; }
          return (
            <div key={it.key} className={cn("absolute left-0 flex w-full items-center gap-4 px-3 hover:bg-panel-2/60", rowChanged && "bg-warn/5")} style={{ top: it.start, height: it.size }}>
              <span className={cn("w-[8ch] shrink-0", rowChanged ? "text-warn" : "text-fg-subtle")}>{off.toString(16).toUpperCase().padStart(6, "0")}</span>
              <span className="flex-none whitespace-pre text-fg-muted">{pane(a, off, "a")}</span>
              <span className="flex-none whitespace-pre">{pane(b, off, "b")}</span>
              <span className="flex-none whitespace-pre border-l border-border pl-3">{delta(off)}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
