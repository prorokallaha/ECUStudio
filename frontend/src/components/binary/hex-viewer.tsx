"use client";
import { useEffect, useMemo, useRef } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { PAGE, readWord, useHexPages } from "./use-hex-pages";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

export type HexMode = "modified" | "stock" | "split";
const ROW = 16;

const regionTint: Record<string, string> = { map: "bg-ok/10", axis: "bg-ok/5", candidate: "bg-ai/12", section: "" };

export interface HexViewerProps {
  analysisId: string;
  fileSize: number;
  hasStock: boolean;
  mode: HexMode;
  wordSize: 1 | 2 | 4;
  endian: "Big" | "Little";
  signed: boolean;
  decimal: boolean;
  selected: number | null;
  highlight?: { start: number; length: number } | null;
  scrollTo?: { offset: number; seq: number } | null;
  bookmarks: number[];
  onSelect: (offset: number) => void;
  loader: ReturnType<typeof useHexPages>;
}

/** Virtualised hex/ASCII view. Only visible rows are rendered and only visible 4 KB pages are fetched. */
export function HexViewer(p: HexViewerProps) {
  const t = useT();
  const parent = useRef<HTMLDivElement>(null);
  const rows = Math.ceil(p.fileSize / ROW);
  const v = useVirtualizer({ count: rows, getScrollElement: () => parent.current, estimateSize: () => 20, overscan: 20 });
  const items = v.getVirtualItems();
  const { ensure, byteAt, regionsAt } = p.loader;

  const first = items[0]?.index ?? 0;
  const last = items.at(-1)?.index ?? 0;
  useEffect(() => {
    const a = Math.floor((first * ROW) / PAGE), b = Math.floor((last * ROW) / PAGE);
    const need: number[] = [];
    for (let i = a; i <= b; i++) need.push(i);
    ensure(need);
  }, [first, last, ensure]);

  useEffect(() => {
    if (p.scrollTo) v.scrollToIndex(Math.floor(p.scrollTo.offset / ROW), { align: "center" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.scrollTo?.seq]);

  const bookmarkSet = useMemo(() => new Set(p.bookmarks), [p.bookmarks]);
  const words = ROW / p.wordSize;
  const cellW = p.decimal ? (p.wordSize === 1 ? "w-[3.2ch]" : p.wordSize === 2 ? "w-[6.4ch]" : "w-[11.5ch]") : (p.wordSize === 1 ? "w-[2.4ch]" : p.wordSize === 2 ? "w-[4.6ch]" : "w-[9ch]");

  const renderBytes = (rowOff: number, which: "mod" | "stock") => {
    const cells = [];
    for (let w = 0; w < words; w++) {
      const off = rowOff + w * p.wordSize;
      if (off >= p.fileSize) break;
      const raw: (number | null)[] = [];
      let changed = false;
      for (let k = 0; k < p.wordSize; k++) {
        const b = byteAt(off + k);
        const val = b ? (which === "mod" ? b.mod : b.stock) : null;
        raw.push(val);
        if (b && b.stock !== null && b.stock !== b.mod) changed = true;
      }
      const ordered = p.endian === "Big" ? raw : [...raw].reverse();
      const text = raw.some((x) => x === null) ? "··".repeat(p.wordSize)
        : p.decimal ? String(readWord(raw, p.wordSize, p.endian, p.signed))
        : ordered.map((x) => x!.toString(16).toUpperCase().padStart(2, "0")).join("");
      const region = regionsAt(off).find((r) => r.kind !== "section");
      const isSel = p.selected !== null && p.selected >= off && p.selected < off + p.wordSize;
      const inHi = p.highlight && off >= p.highlight.start && off < p.highlight.start + p.highlight.length;
      cells.push(
        <span
          key={w}
          onMouseDown={() => p.onSelect(off)}
          className={cn(
            "inline-block cursor-pointer text-right tabular-nums", cellW,
            region && regionTint[region.kind],
            changed && (which === "mod" ? "text-warn font-semibold" : "text-stock line-through decoration-warn/60"),
            inHi && "bg-attn/25",
            isSel && "bg-calc text-white rounded-sm",
            w % (8 / p.wordSize) === 0 && w > 0 && "ml-2",
          )}
        >{text}</span>,
      );
    }
    return cells;
  };

  const renderAscii = (rowOff: number) => {
    let s = "";
    for (let i = 0; i < ROW && rowOff + i < p.fileSize; i++) {
      const b = byteAt(rowOff + i)?.mod;
      s += b === undefined ? " " : b >= 32 && b < 127 ? String.fromCharCode(b) : "·";
    }
    return s;
  };

  return (
    <div ref={parent} className="h-full overflow-auto font-mono text-[12px] leading-5" tabIndex={0}
      onKeyDown={(e) => {
        if (p.selected === null) return;
        const step = { ArrowRight: p.wordSize, ArrowLeft: -p.wordSize, ArrowDown: ROW, ArrowUp: -ROW, PageDown: ROW * 32, PageUp: -ROW * 32 }[e.key];
        if (step) { e.preventDefault(); const n = Math.max(0, Math.min(p.fileSize - 1, p.selected + step)); p.onSelect(n); v.scrollToIndex(Math.floor(n / ROW)); }
      }}
    >
      <div className="sticky top-0 z-10 flex gap-4 border-b border-border bg-bg-elev px-3 py-1 text-[11px] text-fg-subtle">
        <span className="w-[8ch]">{t("binary.offset")}</span>
        {(p.mode === "split" ? [t("common.stock"), t("common.modified")] : [p.mode === "stock" ? t("common.stock") : t("common.modified")]).map((h) => (
          <span key={h} className="flex-none">{h} {Array.from({ length: words }, (_, i) => (i * p.wordSize).toString(16).toUpperCase()).join(" ")}</span>
        ))}
        {p.mode !== "split" && <span>ASCII</span>}
      </div>
      <div style={{ height: v.getTotalSize(), position: "relative" }}>
        {items.map((it) => {
          const off = it.index * ROW;
          return (
            <div key={it.key} className="absolute left-0 flex w-full items-center gap-4 px-3 hover:bg-panel-2/60" style={{ top: it.start, height: it.size }}>
              <span className={cn("w-[8ch] shrink-0 text-fg-subtle", bookmarkSet.has(off - (off % ROW)) && "text-attn")}>{off.toString(16).toUpperCase().padStart(6, "0")}</span>
              {p.mode === "split" && p.hasStock && <span className="flex-none whitespace-pre text-fg-muted">{renderBytes(off, "stock")}</span>}
              <span className="flex-none whitespace-pre">{renderBytes(off, p.mode === "stock" && p.hasStock ? "stock" : "mod")}</span>
              {p.mode !== "split" && <span className="whitespace-pre text-fg-subtle">{renderAscii(off)}</span>}
            </div>
          );
        })}
      </div>
    </div>
  );
}
