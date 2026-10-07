"use client";
import { memo } from "react";
import { cssVar } from "@/lib/colors";
import { deltaColor } from "@/components/charts/theme";
import { cn } from "@/lib/cn";

export interface CellRef { row: number; col: number }

export interface MapGridProps {
  xAxis: number[];
  yAxis: number[];
  values: number[];
  stock?: number[] | null;
  kind: "values" | "stock" | "delta" | "deltaPct";
  unit?: string;
  selected: CellRef[];
  onSelect: (cells: CellRef[], additive: boolean) => void;
  onlyChanged?: boolean;
  dense?: boolean;
  /** Decimals implied by the map's scaling factor (e.g. factor 0.1 → 1 decimal). */
  decimals?: number;
}

function fmtAuto(v: number) {
  const a = Math.abs(v);
  return a >= 1000 ? v.toFixed(0) : a >= 100 ? v.toFixed(1) : a >= 10 ? v.toFixed(2) : v.toFixed(3);
}

/** Calibration table: rows = Y axis, columns = X axis, values row-major over Y. Heat or delta colouring. */
export const MapGrid = memo(function MapGrid(p: MapGridProps) {
  const fmt = (v: number) => (p.decimals !== undefined ? v.toFixed(p.decimals) : fmtAuto(v));
  const fmtAxis = (v: number) => (Number.isInteger(v) ? String(v) : fmtAuto(v));
  const cols = p.xAxis.length || 1;
  const rows = p.yAxis.length || 1;
  const vals = p.values;
  let min = Infinity, max = -Infinity;
  for (const v of vals) { if (v < min) min = v; if (v > max) max = v; }
  const span = max - min || 1;
  const isSel = (r: number, c: number) => p.selected.some((s) => s.row === r && s.col === c);
  const rowChanged = (r: number) => !p.stock || p.xAxis.some((_, c) => p.stock![r * cols + c] !== vals[r * cols + c]);

  const cellValue = (i: number): { text: string; bg: string; changed: boolean } => {
    const s = p.stock?.[i];
    const m = vals[i];
    const changed = s !== undefined && s !== null && Math.abs(s - m) > 1e-9;
    if (p.kind === "delta" || p.kind === "deltaPct") {
      if (s === undefined || s === null) return { text: "—", bg: "transparent", changed: false };
      const d = m - s;
      const pct = s !== 0 ? (d / Math.abs(s)) * 100 : d === 0 ? 0 : 100;
      return { text: !changed ? "·" : p.kind === "delta" ? (d > 0 ? "+" : "") + fmt(d) : (pct > 0 ? "+" : "") + pct.toFixed(1) + "%", bg: deltaColor(pct), changed };
    }
    const v = p.kind === "stock" ? (s ?? m) : m;
    const t = (v - min) / span;
    return { text: fmt(v), bg: cssVar(t > 0.66 ? "warn" : t > 0.33 ? "attn" : "calc", 0.08 + t * 0.22), changed };
  };

  return (
    <div className="overflow-auto">
      <table className={cn("border-separate border-spacing-0 font-mono", p.dense ? "text-[10px]" : "text-[11px]")}>
        <thead>
          <tr>
            <th className="sticky left-0 top-0 z-20 bg-panel-2 px-1.5 py-1 text-[9px] font-normal text-fg-subtle">Y \ X</th>
            {p.xAxis.map((x, c) => (
              <th key={c} className="sticky top-0 z-10 border-b border-border bg-panel-2 px-1.5 py-1 text-right font-medium text-fg-muted">{fmtAxis(x)}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {Array.from({ length: rows }, (_, r) => {
            if (p.onlyChanged && !rowChanged(r)) return null;
            return (
              <tr key={r}>
                <th className="sticky left-0 z-10 border-r border-border bg-panel-2 px-1.5 text-right font-medium text-fg-muted">{fmtAxis(p.yAxis[r] ?? r)}</th>
                {p.xAxis.map((_, c) => {
                  const i = r * cols + c;
                  const cv = cellValue(i);
                  return (
                    <td
                      key={c}
                      onMouseDown={(e) => p.onSelect([{ row: r, col: c }], e.shiftKey || e.ctrlKey || e.metaKey)}
                      style={{ background: cv.bg }}
                      className={cn(
                        "cursor-cell border-b border-r border-border/40 px-1.5 py-[3px] text-right tabular-nums",
                        p.onlyChanged && !cv.changed && "opacity-30",
                        cv.changed && p.kind === "values" && "font-semibold text-fg",
                        isSel(r, c) && "outline outline-2 -outline-offset-2 outline-calc",
                      )}
                    >{cv.text}</td>
                  );
                })}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
});
