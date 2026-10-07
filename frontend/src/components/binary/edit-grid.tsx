"use client";
import { forwardRef, useEffect, useImperativeHandle, useRef, type KeyboardEvent, type ReactNode } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { ByteOrder, NumBase, WordKind } from "@/types/editing";
import { ROW, asciiChar, cellText, cellWidth, wordBytes, type RowLayout } from "./row-model";
import { FoldRow } from "./fold-row";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

export interface EditGridProps {
  size: number;
  layout: RowLayout;
  onToggleFold: (start: number) => void;
  /** Working byte (null = not loaded yet). */
  working: (offset: number) => number | null;
  original: (offset: number) => number | null;
  isChanged: (offset: number) => boolean;
  /** Typed-but-not-written byte at offset, if any (shown distinctly; not yet in the working buffer). */
  pendingAt: (offset: number) => number | null;
  word: WordKind;
  base: NumBase;
  signed: boolean;
  order: ByteOrder;
  showAscii: boolean;
  selection: { start: number; end: number } | null;
  cursor: number | null;
  scrollTo: { offset: number; seq: number } | null;
  onVisible: (from: number, to: number) => void;
  onCellDown: (offset: number, extend: boolean) => void;
  onKeyDown: (e: KeyboardEvent<HTMLDivElement>) => void;
}

export interface EditGridHandle { focus: () => void; scrollToOffset: (offset: number) => void }

/**
 * Virtualised hex view of the working buffer. Renders only visible rows; folded padding rows are visual only.
 * The display interpretation (word size, base, sign, byte order) never changes the bytes.
 */
export const EditGrid = forwardRef<EditGridHandle, EditGridProps>(function EditGrid(p, ref) {
  const t = useT();
  const parent = useRef<HTMLDivElement>(null);
  const v = useVirtualizer({ count: p.layout.count, getScrollElement: () => parent.current, estimateSize: () => 20, overscan: 16 });
  const items = v.getVirtualItems();
  const n = wordBytes(p.word);
  const words = ROW / n;
  const cw = cellWidth(p.word, p.base, p.signed);

  useImperativeHandle(ref, () => ({
    focus: () => parent.current?.focus(),
    scrollToOffset: (o: number) => v.scrollToIndex(p.layout.indexOf(o), { align: "auto" }),
  }), [v, p.layout]);

  const firstIdx = items[0]?.index ?? 0, lastIdx = items.at(-1)?.index ?? 0;
  const { onVisible, layout } = p;
  useEffect(() => {
    if (!layout.count) return;
    const a = layout.rowAt(firstIdx), z = layout.rowAt(lastIdx);
    const from = a.kind === "data" ? a.offset : a.start;
    const to = z.kind === "data" ? z.offset + ROW : z.kind === "fold" ? z.start : z.end;
    onVisible(from, Math.max(from, to));
  }, [firstIdx, lastIdx, layout, onVisible]);

  useEffect(() => {
    if (p.scrollTo) v.scrollToIndex(p.layout.indexOf(p.scrollTo.offset), { align: "center" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.scrollTo?.seq]);

  const renderRow = (rowOff: number) => {
    const cells = [];
    let rowChanged = false;
    for (let w = 0; w < words; w++) {
      const off = rowOff + w * n;
      if (off >= p.size) break;
      const bytes: (number | null)[] = [];
      let changed = false, pending = false;
      const orig: (number | null)[] = [];
      for (let k = 0; k < n; k++) {
        const o = off + k;
        if (o >= p.size) { bytes.push(null); orig.push(null); continue; }
        const pv = p.pendingAt(o);
        if (pv !== null) pending = true;
        bytes.push(pv ?? p.working(o));
        orig.push(p.original(o));
        if (p.isChanged(o)) changed = true;
      }
      if (changed) rowChanged = true;
      const text = cellText(bytes, p.word, p.base, p.signed, p.order);
      const sel = p.selection && off < p.selection.end && off + n > p.selection.start;
      const isCur = p.cursor !== null && p.cursor >= off && p.cursor < off + n;
      cells.push(
        <span key={w} data-off={off}
          title={changed ? t("editor.cellOriginal", { value: cellText(orig, p.word, p.base, p.signed, p.order) }) : undefined}
          onMouseDown={(e) => { e.preventDefault(); parent.current?.focus(); p.onCellDown(off, e.shiftKey); }}
          className={cn(
            "inline-block cursor-pointer rounded-sm text-right tabular-nums", cw,
            changed && "bg-warn/12 font-semibold text-warn",
            sel && "bg-calc/25",
            pending && "bg-attn/35 text-fg underline decoration-attn",
            isCur && "outline outline-1 outline-calc",
            w % Math.max(1, 8 / n) === 0 && w > 0 && "ml-2",
          )}
        >{text}</span>,
      );
    }
    let ascii: ReactNode = null;
    if (p.showAscii) {
      if (!rowChanged) {
        let s = "";
        for (let i = 0; i < ROW && rowOff + i < p.size; i++) s += asciiChar(p.pendingAt(rowOff + i) ?? p.working(rowOff + i));
        ascii = <span className="whitespace-pre text-fg-subtle">{s}</span>;
      } else {
        const chars = [];
        for (let i = 0; i < ROW && rowOff + i < p.size; i++) {
          const o = rowOff + i;
          chars.push(<span key={i} className={p.isChanged(o) ? "text-warn" : undefined}>{asciiChar(p.pendingAt(o) ?? p.working(o))}</span>);
        }
        ascii = <span className="whitespace-pre text-fg-subtle">{chars}</span>;
      }
    }
    return { cells, ascii, rowChanged };
  };

  return (
    <div ref={parent} className="h-full overflow-auto font-mono text-[12px] leading-5 outline-none focus-visible:ring-1 focus-visible:ring-calc/40" tabIndex={0} onKeyDown={p.onKeyDown}>
      <div className="sticky top-0 z-10 flex gap-4 border-b border-border bg-bg-elev px-3 py-1 text-[11px] text-fg-subtle">
        <span className="w-[8ch] shrink-0">{t("binary.offset")}</span>
        <span className="flex-none whitespace-pre">
          {Array.from({ length: words }, (_, w) => (
            <span key={w} className={cn("inline-block text-right", cw, w % Math.max(1, 8 / n) === 0 && w > 0 && "ml-2")}>{(w * n).toString(16).toUpperCase()}</span>
          ))}
        </span>
        {p.showAscii && <span>ASCII</span>}
      </div>
      <div style={{ height: v.getTotalSize(), position: "relative" }}>
        {items.map((it) => {
          const row = p.layout.rowAt(it.index);
          if (row.kind !== "data") {
            return (
              <div key={it.key} className="absolute left-0 flex w-full items-center px-3 py-px" style={{ top: it.start, height: it.size }}>
                <FoldRow start={row.start} end={row.end} value={row.value} expanded={row.kind === "unfold"} onToggle={() => p.onToggleFold(row.start)} />
              </div>
            );
          }
          const off = row.offset;
          const r = renderRow(off);
          return (
            <div key={it.key} className="absolute left-0 flex w-full items-center gap-4 px-3 hover:bg-panel-2/60" style={{ top: it.start, height: it.size }}>
              <span className={cn("w-[8ch] shrink-0", r.rowChanged ? "text-warn" : "text-fg-subtle")}>{off.toString(16).toUpperCase().padStart(6, "0")}</span>
              <span className="flex-none whitespace-pre">{r.cells}</span>
              {r.ascii}
            </div>
          );
        })}
      </div>
    </div>
  );
});
