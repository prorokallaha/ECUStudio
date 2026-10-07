import { useCallback, useMemo, useState } from "react";
import type { AnalysisReport } from "@/types/domain";
import type { ByteOrder, ByteRange, DiffRange, DisplayRow, NumBase, PaddingRun, WordKind } from "@/types/editing";

/** Bytes per hex row. Rows always start at a multiple of ROW in the original file. */
export const ROW = 16;
/** Minimal run of identical 0x00 / 0xFF bytes that is folded into one visual row. */
export const MIN_PADDING = 256;

// ─── Padding detection ──────────────────────────────────────────────────────────────────────────────────────────────

/**
 * Finds row-aligned runs (≥ MIN_PADDING bytes) where every byte is 0x00 or every byte is 0xFF.
 * With `b`, a row only counts when both buffers hold the same padding there (so a folded row never hides a difference).
 * `skipRows[r] = 1` excludes row r (e.g. rows touched by unsaved edits). Purely an analysis of the bytes: nothing is changed.
 */
export function findPaddingRuns(a: Uint8Array, size: number, opts?: { b?: Uint8Array; skipRows?: Uint8Array; minBytes?: number }): PaddingRun[] {
  const b = opts?.b;
  const skip = opts?.skipRows;
  const minRows = Math.ceil((opts?.minBytes ?? MIN_PADDING) / ROW);
  const rows = Math.floor(Math.min(size, a.length, b ? b.length : Infinity) / ROW);
  const runs: PaddingRun[] = [];
  let runStart = -1, runVal = -1;
  const close = (endRow: number) => {
    if (runVal >= 0 && endRow - runStart >= minRows) runs.push({ start: runStart * ROW, end: endRow * ROW, value: runVal });
  };
  for (let r = 0; r < rows; r++) {
    const o = r * ROW;
    let v = a[o];
    if ((v !== 0x00 && v !== 0xff) || (skip && skip[r])) v = -1;
    else {
      for (let k = 1; k < ROW; k++) if (a[o + k] !== v) { v = -1; break; }
      if (v >= 0 && b) for (let k = 0; k < ROW; k++) if (b[o + k] !== v) { v = -1; break; }
    }
    if (v === runVal && v >= 0) continue;
    close(r);
    runStart = r; runVal = v;
  }
  close(rows);
  return runs;
}

/** Row mask (1 = row touches one of the ranges). */
export function rowMask(size: number, ranges: { start: number; length: number }[]): Uint8Array {
  const m = new Uint8Array(Math.ceil(size / ROW));
  for (const r of ranges) {
    const a = Math.floor(r.start / ROW), z = Math.floor((r.start + Math.max(1, r.length) - 1) / ROW);
    for (let i = Math.max(0, a); i <= z && i < m.length; i++) m[i] = 1;
  }
  return m;
}

// ─── Row layout (visual only) ───────────────────────────────────────────────────────────────────────────────────────

interface Segment { idx: number; kind: "data" | "fold" | "unfold"; offset: number; rows: number; run?: PaddingRun }

/**
 * Maps visual row indexes ↔ original file offsets. Folded padding runs collapse into one row; nothing about the
 * data or the offsets changes, addresses shown are always the original file offsets.
 */
export class RowLayout {
  readonly segments: Segment[] = [];
  readonly count: number;

  constructor(readonly fileSize: number, runs: PaddingRun[], expanded: ReadonlySet<number>) {
    let idx = 0, cur = 0;
    const data = (from: number, to: number) => {
      const rows = Math.ceil((to - from) / ROW);
      if (rows > 0) { this.segments.push({ idx, kind: "data", offset: from, rows }); idx += rows; }
    };
    for (const run of runs) {
      if (run.start < cur || run.end > fileSize) continue;
      data(cur, run.start);
      if (expanded.has(run.start)) {
        this.segments.push({ idx, kind: "unfold", offset: run.start, rows: 1, run }); idx += 1;
        data(run.start, run.end);
      } else {
        this.segments.push({ idx, kind: "fold", offset: run.start, rows: 1, run }); idx += 1;
      }
      cur = run.end;
    }
    data(cur, fileSize);
    this.count = idx;
  }

  private segAtIndex(i: number): Segment | undefined {
    const s = this.segments;
    let lo = 0, hi = s.length - 1;
    while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (s[mid].idx <= i) lo = mid; else hi = mid - 1; }
    return s[lo];
  }

  rowAt(i: number): DisplayRow {
    const seg = this.segAtIndex(i);
    if (!seg) return { kind: "data", offset: 0 };
    if (seg.kind === "data") return { kind: "data", offset: seg.offset + (i - seg.idx) * ROW };
    return { kind: seg.kind, start: seg.run!.start, end: seg.run!.end, value: seg.run!.value };
  }

  /** Visual row index that shows `offset` (the fold row when the offset lies in a folded run). */
  indexOf(offset: number): number {
    for (const seg of this.segments) {
      if (seg.kind === "unfold") continue;
      const end = seg.kind === "fold" ? seg.run!.end : seg.offset + seg.rows * ROW;
      if (offset >= seg.offset && offset < end) return seg.kind === "fold" ? seg.idx : seg.idx + Math.floor((offset - seg.offset) / ROW);
    }
    return Math.max(0, this.count - 1);
  }

  /** The folded run that hides `offset`, if any. */
  foldedRunAt(offset: number): PaddingRun | null {
    for (const seg of this.segments) if (seg.kind === "fold" && offset >= seg.run!.start && offset < seg.run!.end) return seg.run!;
    return null;
  }
}

/** Layout + expand state. `enabled=false` (setting off) shows every row. */
export function useRowLayout(fileSize: number, runs: PaddingRun[], enabled: boolean) {
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set());
  const layout = useMemo(() => new RowLayout(fileSize, enabled ? runs : [], expanded), [fileSize, runs, enabled, expanded]);
  const toggle = useCallback((start: number) => setExpanded((s) => { const n = new Set(s); if (n.has(start)) n.delete(start); else n.add(start); return n; }), []);
  /** Expands the fold hiding `offset` so it can be shown; returns the layout that will contain it. */
  const reveal = useCallback((offset: number) => {
    const run = layout.foldedRunAt(offset);
    if (!run) return layout;
    const next = new Set(expanded); next.add(run.start);
    setExpanded(next);
    return new RowLayout(fileSize, enabled ? runs : [], next);
  }, [layout, expanded, fileSize, runs, enabled]);
  return { layout, toggle, reveal };
}

// ─── Ranges ─────────────────────────────────────────────────────────────────────────────────────────────────────────

/** Sorted ranges → fast "is offset inside any range" lookups. */
export function rangeIndex(ranges: { start: number; end: number }[]) {
  const sorted = [...ranges].sort((a, b) => a.start - b.start);
  const starts = sorted.map((r) => r.start);
  /** Index of the last range starting at or before offset, or -1. */
  const at = (offset: number) => {
    let lo = 0, hi = starts.length - 1, ans = -1;
    while (lo <= hi) { const mid = (lo + hi) >> 1; if (starts[mid] <= offset) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
    return ans;
  };
  return {
    sorted,
    /** Index of the last range starting at or before offset, or -1. */
    indexAt: at,
    has: (offset: number) => { const i = at(offset); return i >= 0 && offset < sorted[i].end; },
    find: (offset: number) => { const i = at(offset); return i >= 0 && offset < sorted[i].end ? sorted[i] : null; },
    /** First range strictly after `offset` (wraps when `wrap`). */
    next: (offset: number, wrap = true) => { const i = at(offset); return sorted[i + 1] ?? (wrap ? sorted[0] ?? null : null); },
    /** Last range starting strictly before `offset` (wraps when `wrap`). */
    prev: (offset: number, wrap = true) => sorted[at(offset - 1)] ?? (wrap ? sorted.at(-1) ?? null : null),
  };
}

export const byteRangesToRanges = (r: ByteRange[]) => r.map((x) => ({ start: x.start, end: x.start + x.length }));

/** Byte-by-byte comparison of two buffers. Bytes beyond the shorter buffer count as different. */
export function diffBuffers(a: Uint8Array, b: Uint8Array): { ranges: DiffRange[]; changed: number; size: number } {
  const n = Math.min(a.length, b.length), size = Math.max(a.length, b.length);
  const ranges: DiffRange[] = [];
  let changed = 0, start = -1;
  for (let i = 0; i < n; i++) {
    if (a[i] !== b[i]) { changed++; if (start < 0) start = i; }
    else if (start >= 0) { ranges.push({ start, end: i }); start = -1; }
  }
  if (start >= 0) ranges.push({ start, end: n });
  if (size > n) { ranges.push({ start: n, end: size }); changed += size - n; }
  return { ranges, changed, size };
}

// ─── Value interpretation (display only) ────────────────────────────────────────────────────────────────────────────

export const wordBytes = (w: WordKind): 1 | 2 | 4 => (w === "8" ? 1 : w === "16" ? 2 : 4);

/** Reads a value from bytes given in file order. Display only. */
export function decode(bytes: ArrayLike<number | null>, word: WordKind, signed: boolean, order: ByteOrder): number | null {
  const n = wordBytes(word);
  if (bytes.length < n) return null;
  const buf = new DataView(new ArrayBuffer(4));
  for (let i = 0; i < n; i++) { const v = bytes[i]; if (v === null || v === undefined) return null; buf.setUint8(i, v); }
  const le = order === "Little";
  if (word === "f32") return buf.getFloat32(0, le);
  if (n === 1) return signed ? buf.getInt8(0) : buf.getUint8(0);
  if (n === 2) return signed ? buf.getInt16(0, le) : buf.getUint16(0, le);
  return signed ? buf.getInt32(0, le) : buf.getUint32(0, le);
}

/** Encodes a typed value into bytes in file order, or null when out of range / not a number. Used only for an explicit write. */
export function encode(text: string, word: WordKind, signed: boolean, order: ByteOrder): Uint8Array | null {
  const s = text.trim();
  if (!s) return null;
  const n = wordBytes(word);
  const buf = new DataView(new ArrayBuffer(n));
  const le = order === "Little";
  if (word === "f32") {
    const f = Number(s);
    if (!Number.isFinite(f)) return null;
    buf.setFloat32(0, f, le);
  } else {
    const neg = s.startsWith("-");
    const body = neg ? s.slice(1) : s;
    const v = /^0x[0-9a-f]+$/i.test(body) ? parseInt(body.slice(2), 16) : /^\d+$/.test(body) ? Number(body) : NaN;
    if (!Number.isFinite(v)) return null;
    const val = neg ? -v : v;
    const bits = n * 8;
    const min = signed ? -(2 ** (bits - 1)) : 0, max = signed ? 2 ** (bits - 1) - 1 : 2 ** bits - 1;
    if (val < min || val > max) return null;
    if (n === 1) { if (signed) buf.setInt8(0, val); else buf.setUint8(0, val); }
    else if (n === 2) { if (signed) buf.setInt16(0, val, le); else buf.setUint16(0, val, le); }
    else { if (signed) buf.setInt32(0, val, le); else buf.setUint32(0, val, le); }
  }
  return new Uint8Array(buf.buffer);
}

export const toHex = (bytes: ArrayLike<number>, sep = "") => Array.from(bytes, (b) => b.toString(16).toUpperCase().padStart(2, "0")).join(sep);

export const fmtFloat = (v: number) => (Number.isInteger(v) && Math.abs(v) < 1e9 ? v.toString() : Math.abs(v) >= 1e6 || (Math.abs(v) < 1e-3 && v !== 0) ? v.toExponential(3) : v.toPrecision(6));

/** Cell text for one word (bytes in file order). HEX shows the numeric value's hex (BE = as stored, LE = reversed). */
export function cellText(bytes: (number | null)[], word: WordKind, base: NumBase, signed: boolean, order: ByteOrder): string {
  const n = wordBytes(word);
  if (bytes.length < n || bytes.some((x) => x === null)) return "··".repeat(n);
  if (base === "hex") return toHex(order === "Big" ? (bytes as number[]) : [...(bytes as number[])].reverse());
  const v = decode(bytes, word, signed, order);
  return v === null ? "—" : word === "f32" ? fmtFloat(v) : String(v);
}

/** Tailwind width class for a cell of the given interpretation. */
export function cellWidth(word: WordKind, base: NumBase, signed: boolean): string {
  if (base === "hex") return word === "8" ? "w-[2.4ch]" : word === "16" ? "w-[4.6ch]" : "w-[9ch]";
  if (word === "f32") return "w-[11ch]";
  if (word === "8") return signed ? "w-[4.4ch]" : "w-[3.4ch]";
  if (word === "16") return signed ? "w-[6.6ch]" : "w-[5.6ch]";
  return signed ? "w-[11.6ch]" : "w-[10.6ch]";
}

export const asciiChar = (b: number | null | undefined) => (b === null || b === undefined ? " " : b >= 32 && b < 127 ? String.fromCharCode(b) : "·");

/** "0x41000" — compact hex used in fold labels. */
export const hx = (n: number) => "0x" + n.toString(16).toUpperCase();

// ─── Analysis regions (labels for changed ranges) ───────────────────────────────────────────────────────────────────

export interface AnalysisRegion { start: number; end: number; kind: "map" | "candidate" | "section"; label: string; mapId: string | null }

const typeBytes = (dt: string) => (/8/.test(dt) ? 1 : /32|Float/i.test(dt) ? 4 : 2);

/**
 * Region list derived from the analysis (the same maps / candidates / sections the analysis hex endpoint reports).
 * Valid only for files with the analysed layout (same ECU image size). Candidates stay labelled as candidates.
 */
export function analysisRegions(r: AnalysisReport | null | undefined): AnalysisRegion[] {
  if (!r) return [];
  const out: AnalysisRegion[] = [];
  for (const m of r.maps) out.push({ start: m.address, end: m.address + m.rows * m.cols * typeBytes(m.dataType), kind: "map", label: m.name, mapId: m.id });
  for (const c of r.candidates) if (c.address != null) out.push({ start: c.address, end: c.address + (c.rows ?? 1) * (c.cols ?? 1) * typeBytes(c.dataType), kind: "candidate", label: c.id, mapId: null });
  for (const s of r.ecu.sections) out.push({ start: s.start, end: s.end, kind: "section", label: s.kind, mapId: null });
  return out;
}

/** Regions overlapping [start, end), most specific first (maps, candidates, then sections). */
export function regionsFor(regions: AnalysisRegion[], start: number, end: number): AnalysisRegion[] {
  const order = { map: 0, candidate: 1, section: 2 } as const;
  return regions.filter((g) => g.start < end && g.end > start).sort((a, b) => order[a.kind] - order[b.kind]);
}
