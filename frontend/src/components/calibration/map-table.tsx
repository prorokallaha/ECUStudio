"use client";
import { memo, useCallback, useEffect, useMemo, useRef, type ClipboardEvent, type KeyboardEvent, type MouseEvent } from "react";
import { cn } from "@/lib/cn";

/** Selection of a map: one byte per cell (row-major), plus anchor (range start) and focus (cursor) indices. */
export interface CellSelection { mask: Uint8Array; anchor: number; focus: number }

export const emptySelection = (n: number): CellSelection => ({ mask: new Uint8Array(n), anchor: -1, focus: -1 });

export function selectionCount(s: CellSelection): number {
  let n = 0;
  for (let i = 0; i < s.mask.length; i++) n += s.mask[i];
  return n;
}

/** Selected indices in row-major order. */
export function selectedIndices(s: CellSelection): number[] {
  const out: number[] = [];
  for (let i = 0; i < s.mask.length; i++) if (s.mask[i]) out.push(i);
  return out;
}

function rect(cols: number, a: number, b: number, into: Uint8Array) {
  const r0 = Math.min(Math.floor(a / cols), Math.floor(b / cols)), r1 = Math.max(Math.floor(a / cols), Math.floor(b / cols));
  const c0 = Math.min(a % cols, b % cols), c1 = Math.max(a % cols, b % cols);
  for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) into[r * cols + c] = 1;
  return into;
}

export type TableKind = "values" | "stock" | "delta" | "deltaPct";

export interface MapTableProps {
  xAxis: number[];
  yAxis: number[];
  /** Current (working) values, row-major. */
  values: number[];
  /** Analysed values: cells that differ are marked as edited (not re-analysed). */
  base?: number[] | null;
  stock?: number[] | null;
  kind?: TableKind;
  decimals: number;
  heat?: boolean;
  dense?: boolean;
  onlyChanged?: boolean;
  selection: CellSelection;
  onSelection: (s: CellSelection) => void;
  /** Preview of a pending operation: index → stored value. */
  pending?: Map<number, number> | null;
  onCopy?: () => string | null;
  onPaste?: (text: string) => void;
  /** A printable key typed with the table focused (starts value entry). */
  onTypeStart?: (key: string) => void;
  onUndo?: () => void;
  onRedo?: () => void;
  onEscape?: () => void;
}

/** Heat colour: blue (low) → green → yellow → red (high), WinOLS-like; theme independent, alpha keeps text readable. */
const heatBg = (t: number) => `hsl(${Math.round(220 - 220 * t)} 75% 50% / 0.30)`;
const deltaBg = (pct: number) => {
  if (Math.abs(pct) < 0.05) return "transparent";
  const a = 0.15 + Math.min(1, Math.abs(pct) / 30) * 0.5;
  return pct > 0 ? `hsl(${pct > 20 ? 0 : 25} 85% 55% / ${a})` : `hsl(213 85% 58% / ${a})`;
};

const fmtAxis = (v: number) => (Number.isInteger(v) ? String(v) : Math.abs(v) >= 100 ? v.toFixed(1) : v.toFixed(2));

/**
 * Editable calibration table. Event handling is delegated to the table element; rows are memoised and only re-render
 * when their own values, selection or preview change, so drag-selecting over a 32×32 map does not re-render every cell.
 */
export const MapTable = memo(function MapTable(p: MapTableProps) {
  const cols = p.xAxis.length || 1;
  const rows = p.yAxis.length || 1;
  const kind = p.kind ?? "values";
  const wrap = useRef<HTMLDivElement>(null);
  const drag = useRef<{ base: Uint8Array } | null>(null);
  const selRef = useRef(p.selection);
  selRef.current = p.selection;
  const onSel = useRef(p.onSelection);
  onSel.current = p.onSelection;

  useEffect(() => {
    const up = () => { drag.current = null; };
    window.addEventListener("mouseup", up);
    return () => window.removeEventListener("mouseup", up);
  }, []);

  const range = useMemo(() => {
    let min = Infinity, max = -Infinity;
    const src = kind === "stock" && p.stock ? p.stock : p.values;
    for (const v of src) { if (v < min) min = v; if (v > max) max = v; }
    return { min, span: max - min || 1 };
  }, [p.values, p.stock, kind]);

  // Per-row keys: memoised rows compare these strings instead of the whole mask.
  const rowSel = useMemo(() => {
    const keys: string[] = new Array(rows).fill("");
    const m = p.selection.mask;
    for (let i = 0; i < m.length; i++) if (m[i]) keys[Math.floor(i / cols)] += i % cols + ",";
    const f = p.selection.focus;
    if (f >= 0) keys[Math.floor(f / cols)] += "f" + (f % cols);
    return keys;
  }, [p.selection, rows, cols]);

  const rowPending = useMemo(() => {
    const keys: string[] = new Array(rows).fill("");
    p.pending?.forEach((v, i) => { keys[Math.floor(i / cols)] += `${i % cols}:${v},`; });
    return keys;
  }, [p.pending, rows, cols]);

  const cellIndex = (e: MouseEvent): { i?: number; row?: number; col?: number; all?: boolean } | null => {
    const el = (e.target as HTMLElement).closest<HTMLElement>("[data-i],[data-row],[data-col],[data-all]");
    if (!el) return null;
    if (el.dataset.i !== undefined) return { i: Number(el.dataset.i) };
    if (el.dataset.row !== undefined) return { row: Number(el.dataset.row) };
    if (el.dataset.col !== undefined) return { col: Number(el.dataset.col) };
    return { all: true };
  };

  const onMouseDown = (e: MouseEvent) => {
    if (e.button !== 0) return;
    const hit = cellIndex(e);
    if (!hit) return;
    e.preventDefault();
    wrap.current?.focus({ preventScroll: true });
    const cur = selRef.current;
    const n = rows * cols;
    const additive = e.ctrlKey || e.metaKey;
    if (hit.all) { onSel.current({ mask: new Uint8Array(n).fill(1), anchor: 0, focus: n - 1 }); return; }
    if (hit.row !== undefined || hit.col !== undefined) {
      const mask = additive ? cur.mask.slice() : new Uint8Array(n);
      const a = hit.row !== undefined ? hit.row * cols : hit.col!;
      const b = hit.row !== undefined ? hit.row * cols + cols - 1 : (rows - 1) * cols + hit.col!;
      if (e.shiftKey && cur.anchor >= 0) {
        const ar = Math.floor(cur.anchor / cols), ac = cur.anchor % cols;
        if (hit.row !== undefined) rect(cols, Math.min(ar, hit.row) * cols, Math.max(ar, hit.row) * cols + cols - 1, mask);
        else rect(cols, Math.min(ac, hit.col!), (rows - 1) * cols + Math.max(ac, hit.col!), mask);
        onSel.current({ mask, anchor: cur.anchor, focus: b });
      } else onSel.current({ mask: rect(cols, a, b, mask), anchor: a, focus: b });
      return;
    }
    const i = hit.i!;
    if (e.shiftKey && cur.anchor >= 0) {
      const mask = rect(cols, cur.anchor, i, additive ? cur.mask.slice() : new Uint8Array(n));
      drag.current = { base: additive ? cur.mask.slice() : new Uint8Array(n) };
      onSel.current({ mask, anchor: cur.anchor, focus: i });
    } else if (additive) {
      const mask = cur.mask.slice();
      mask[i] = mask[i] ? 0 : 1;
      drag.current = { base: mask.slice() };
      onSel.current({ mask, anchor: i, focus: i });
    } else {
      const mask = new Uint8Array(n);
      mask[i] = 1;
      drag.current = { base: new Uint8Array(n) };
      onSel.current({ mask, anchor: i, focus: i });
    }
  };

  const onMouseOver = (e: MouseEvent) => {
    if (!drag.current) return;
    const hit = cellIndex(e);
    if (hit?.i === undefined) return;
    const cur = selRef.current;
    if (cur.focus === hit.i) return;
    onSel.current({ mask: rect(cols, cur.anchor, hit.i, drag.current.base.slice()), anchor: cur.anchor, focus: hit.i });
  };

  const onKeyDown = (e: KeyboardEvent) => {
    const mod = e.ctrlKey || e.metaKey;
    const cur = selRef.current;
    const n = rows * cols;
    if (mod && e.key.toLowerCase() === "a") { e.preventDefault(); onSel.current({ mask: new Uint8Array(n).fill(1), anchor: 0, focus: n - 1 }); return; }
    if (mod && e.key.toLowerCase() === "z" && p.onUndo) { e.preventDefault(); e.stopPropagation(); (e.shiftKey ? p.onRedo : p.onUndo)?.(); return; }
    if (mod && e.key.toLowerCase() === "y" && p.onRedo) { e.preventDefault(); e.stopPropagation(); p.onRedo(); return; }
    if (e.key === "Escape") { p.onEscape?.(); return; }
    const moves: Record<string, [number, number]> = { ArrowUp: [-1, 0], ArrowDown: [1, 0], ArrowLeft: [0, -1], ArrowRight: [0, 1] };
    const mv = moves[e.key];
    if (mv) {
      e.preventDefault();
      const f = cur.focus >= 0 ? cur.focus : 0;
      const r = Math.max(0, Math.min(rows - 1, Math.floor(f / cols) + mv[0]));
      const c = Math.max(0, Math.min(cols - 1, (f % cols) + mv[1]));
      const i = r * cols + c;
      if (e.shiftKey && cur.anchor >= 0) onSel.current({ mask: rect(cols, cur.anchor, i, new Uint8Array(n)), anchor: cur.anchor, focus: i });
      else { const mask = new Uint8Array(n); mask[i] = 1; onSel.current({ mask, anchor: i, focus: i }); }
      wrap.current?.querySelector<HTMLElement>(`[data-i="${i}"]`)?.scrollIntoView({ block: "nearest", inline: "nearest" });
      return;
    }
    if (!mod && !e.altKey && p.onTypeStart && (/^[0-9.,\-+*/%=]$/.test(e.key) || e.key === "Enter" || e.key === "F2")) {
      e.preventDefault();
      p.onTypeStart(e.key === "Enter" || e.key === "F2" ? "" : e.key);
    }
  };

  const copyRef = useRef(p.onCopy);
  copyRef.current = p.onCopy;
  const pasteRef = useRef(p.onPaste);
  pasteRef.current = p.onPaste;
  const onCopyEvt = useCallback((e: ClipboardEvent) => {
    const text = copyRef.current?.();
    if (text == null) return;
    e.preventDefault();
    e.clipboardData.setData("text/plain", text);
  }, []);
  const onPasteEvt = useCallback((e: ClipboardEvent) => {
    if (!pasteRef.current) return;
    e.preventDefault();
    pasteRef.current(e.clipboardData.getData("text/plain"));
  }, []);

  const selCols = useMemo(() => {
    const s = new Set<number>();
    const m = p.selection.mask;
    for (let i = 0; i < m.length; i++) if (m[i]) s.add(i % cols);
    return s;
  }, [p.selection, cols]);

  return (
    <div ref={wrap} tabIndex={0} onKeyDown={onKeyDown} onCopy={onCopyEvt} onPaste={onPasteEvt}
      className="max-h-full overflow-auto rounded outline-none focus-visible:ring-1 focus-visible:ring-calc/40">
      <table onMouseDown={onMouseDown} onMouseOver={onMouseOver}
        className={cn("select-none border-separate border-spacing-0 font-mono", p.dense ? "text-[10px]" : "text-[11px]")}>
        <thead>
          <tr>
            <th data-all="" className="sticky left-0 top-0 z-20 cursor-pointer bg-panel-2 px-1.5 py-1 text-[9px] font-normal text-fg-subtle">Y \ X</th>
            {p.xAxis.map((x, c) => (
              <th key={c} data-col={c} className={cn("sticky top-0 z-10 cursor-s-resize border-b border-border bg-panel-2 px-1.5 py-1 text-right font-medium text-fg-muted", selCols.has(c) && "text-calc")}>{fmtAxis(x)}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {Array.from({ length: rows }, (_, r) => (
            <Row key={r} r={r} cols={cols} y={p.yAxis[r] ?? r} values={p.values} base={p.base ?? null} stock={p.stock ?? null} kind={kind}
              decimals={p.decimals} heat={p.heat ?? true} min={range.min} span={range.span} selKey={rowSel[r]} mask={p.selection.mask}
              focus={p.selection.focus} pendingKey={rowPending[r]} pending={p.pending ?? null} onlyChanged={!!p.onlyChanged} />
          ))}
        </tbody>
      </table>
    </div>
  );
});

interface RowProps {
  r: number; cols: number; y: number; values: number[]; base: number[] | null; stock: number[] | null; kind: TableKind;
  decimals: number; heat: boolean; min: number; span: number; selKey: string; mask: Uint8Array; focus: number;
  pendingKey: string; pending: Map<number, number> | null; onlyChanged: boolean;
}

const Row = memo(function Row(p: RowProps) {
  const start = p.r * p.cols;
  let rowChanged = false;
  const cells = [];
  for (let c = 0; c < p.cols; c++) {
    const i = start + c;
    const m = p.values[i];
    const s = p.stock?.[i];
    const hasStock = s !== undefined && s !== null;
    const changed = hasStock && Math.abs(s - m) > 1e-9;
    if (changed) rowChanged = true;
    const edited = !!p.base && Math.abs(p.base[i] - m) > 1e-9;
    const pend = p.pending?.get(i);
    let text: string, bg = "transparent";
    if (p.kind === "delta" || p.kind === "deltaPct") {
      if (!hasStock) text = "—";
      else {
        const d = m - s;
        const pct = s !== 0 ? (d / Math.abs(s)) * 100 : d === 0 ? 0 : 100;
        text = !changed ? "·" : p.kind === "delta" ? (d > 0 ? "+" : "") + d.toFixed(p.decimals) : (pct > 0 ? "+" : "") + pct.toFixed(1) + "%";
        bg = deltaBg(pct);
      }
    } else {
      const v = pend ?? (p.kind === "stock" && hasStock ? s : m);
      text = v.toFixed(p.decimals);
      if (p.heat) bg = heatBg(Math.max(0, Math.min(1, (v - p.min) / p.span)));
    }
    const sel = p.mask[i] === 1;
    cells.push(
      <td key={c} data-i={i}
        style={{ background: sel ? "hsl(var(--calc) / 0.42)" : bg, boxShadow: edited && p.kind === "values" ? "inset 0 -2px 0 hsl(var(--attn))" : undefined }}
        className={cn(
          "cursor-cell border-b border-r border-border/40 px-1.5 py-[3px] text-right tabular-nums",
          p.onlyChanged && !changed && "opacity-30",
          changed && p.kind === "values" && "font-semibold text-fg",
          pend !== undefined && "italic text-ai outline outline-1 -outline-offset-1 outline-dashed outline-ai",
          p.focus === i && "outline outline-2 -outline-offset-2 outline-calc",
        )}
      >{text}</td>,
    );
  }
  if (p.onlyChanged && p.stock && !rowChanged) return null;
  const rowSelected = p.selKey.replace(/f\d+$/, "").length > 0;
  return (
    <tr>
      <th data-row={p.r} className={cn("sticky left-0 z-10 cursor-e-resize border-r border-border bg-panel-2 px-1.5 text-right font-medium", rowSelected ? "text-calc" : "text-fg-muted")}>{fmtAxis(p.y)}</th>
      {cells}
    </tr>
  );
}, (a, b) =>
  a.values === b.values && a.base === b.base && a.stock === b.stock && a.kind === b.kind && a.decimals === b.decimals && a.heat === b.heat &&
  a.min === b.min && a.span === b.span && a.selKey === b.selKey && a.pendingKey === b.pendingKey && a.onlyChanged === b.onlyChanged &&
  a.y === b.y && a.cols === b.cols);
