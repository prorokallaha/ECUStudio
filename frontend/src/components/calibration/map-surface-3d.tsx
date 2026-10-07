"use client";
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { ECharts } from "echarts";
import { RotateCcw } from "lucide-react";
import type { SurfaceOverlay } from "@/types/maps-edit";
import { Button, Segmented } from "@/components/ui";
import { chartTheme, tooltipStyle } from "@/components/charts/theme";
import { useUI } from "@/stores/ui";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

const HEAT = ["#1f4e9c", "#1f77b4", "#2ca02c", "#ffbf00", "#ff7f0e", "#d62728"];
const DIVERGING = ["#1f6fd1", "#7fb2ea", "#e8e8e8", "#f5a35c", "#d62728"];

interface Camera { alpha: number; beta: number; distance: number; center?: number[] }
const DEFAULT_CAMERA: Camera = { alpha: 28, beta: 38, distance: 200 };

const strictlyMonotonic = (a: number[]) => a.length < 2 || a.every((v, i) => i === 0 || v > a[i - 1]) || a.every((v, i) => i === 0 || v < a[i - 1]);

export interface MapSurface3DProps {
  xAxis: number[];
  yAxis: number[];
  values: number[];
  stock?: number[] | null;
  unit?: string;
  xName?: string;
  yName?: string;
  decimals: number;
  /** Selected cells (row-major mask), drawn as markers on the surface. */
  mask: Uint8Array;
  onPick: (index: number, additive: boolean) => void;
}

/**
 * 3D surface (echarts-gl): left-drag rotates, wheel zooms, right-drag pans. Perspective / orthographic, wireframe,
 * real or equal axis spacing, overlays (mod, stock, mod + stock, delta). The camera survives data and selection
 * updates (it is read back from grid3dcamerachanged and re-applied), so editing does not reset the view.
 */
export function MapSurface3D(p: MapSurface3DProps) {
  const t = useT();
  const theme = useUI((s) => s.theme);
  const el = useRef<HTMLDivElement>(null);
  const chart = useRef<ECharts | null>(null);
  const camera = useRef<Camera>({ ...DEFAULT_CAMERA });
  const [ready, setReady] = useState(0);
  const hasStock = !!p.stock && p.stock.length === p.values.length;
  const [overlay, setOverlay] = useState<SurfaceOverlay>(hasStock ? "both" : "mod");
  const [wire, setWire] = useState(true);
  const [ortho, setOrtho] = useState(false);
  const [equalUser, setEqual] = useState(false);
  const forcedEqual = !strictlyMonotonic(p.xAxis) || !strictlyMonotonic(p.yAxis);
  const equal = equalUser || forcedEqual;
  const down = useRef<{ x: number; y: number; moved: boolean }>({ x: 0, y: 0, moved: false });
  const pick = useRef(p.onPick);
  pick.current = p.onPick;
  const ov: SurfaceOverlay = hasStock ? overlay : "mod";

  useEffect(() => {
    let disposed = false;
    let ro: ResizeObserver | undefined;
    (async () => {
      const echarts = await import("echarts");
      await import("echarts-gl");
      if (disposed || !el.current) return;
      const c = echarts.init(el.current, undefined, { renderer: "canvas" });
      chart.current = c;
      c.on("grid3dcamerachanged", (e: any) => {
        if (typeof e?.alpha === "number") camera.current = { alpha: e.alpha, beta: e.beta, distance: e.distance, center: e.center };
      });
      c.on("click", (e: any) => {
        // A rotate / pan drag ends with a click event too: only a click without movement selects a cell.
        if (!down.current.moved && e?.seriesIndex === 0 && typeof e.dataIndex === "number" && e.dataIndex >= 0) {
          const ne = e.event?.event as MouseEvent | undefined;
          pick.current(e.dataIndex, !!(ne && (ne.shiftKey || ne.ctrlKey || ne.metaKey)));
        }
      });
      ro = new ResizeObserver(() => c.resize());
      ro.observe(el.current);
      setReady((n) => n + 1);
    })();
    return () => { disposed = true; ro?.disconnect(); chart.current?.dispose(); chart.current = null; };
  }, [theme]);

  const option = useMemo(() => {
    const th = chartTheme();
    const cols = p.xAxis.length || 1, rows = p.yAxis.length || 1;
    const xs = equal ? p.xAxis.map((_, i) => i) : p.xAxis;
    const ys = equal ? p.yAxis.map((_, i) => i) : p.yAxis;
    const delta = p.values.map((v, i) => (hasStock ? v - p.stock![i] : 0));
    const z = ov === "stock" ? p.stock! : ov === "delta" ? delta : p.values;
    const pts = (src: number[]) => {
      const out: number[][] = new Array(rows * cols);
      for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) out[r * cols + c] = [xs[c], ys[r], src[r * cols + c]];
      return out;
    };
    let lo = Infinity, hi = -Infinity;
    for (const v of z) { if (v < lo) lo = v; if (v > hi) hi = v; }
    if (ov === "both") for (const v of p.stock!) { if (v < lo) lo = v; if (v > hi) hi = v; }
    const absMax = Math.max(Math.abs(lo), Math.abs(hi)) || 1;
    const fmt = (v: number) => v.toFixed(p.decimals);
    const axisLabel = (axis: number[]) => (equal
      ? { color: th.subtle, fontSize: 9, formatter: (v: number) => (Number.isInteger(v) && axis[v] !== undefined ? String(axis[v]) : "") }
      : { color: th.subtle, fontSize: 9 });
    const axis3 = (name: string | undefined, axis: number[], n: number) => ({
      type: "value", name, nameTextStyle: { color: th.subtle, fontSize: 10 }, axisLabel: axisLabel(axis),
      axisLine: { lineStyle: { color: th.grid } }, splitLine: { lineStyle: { color: th.grid } }, axisPointer: { label: { show: false } },
      ...(equal ? { min: 0, max: Math.max(1, n - 1), interval: Math.max(1, Math.ceil(n / 10)) } : { min: "dataMin", max: "dataMax" }),
    });
    const tip = (i: number) => {
      const r = Math.floor(i / cols), c = i % cols;
      const m = p.values[i];
      const lines = [`${p.xName ?? "X"} <b>${p.xAxis[c]}</b> · ${p.yName ?? "Y"} <b>${p.yAxis[r]}</b>`, `${t("common.modified")}: <b>${fmt(m)}</b> ${p.unit ?? ""}`];
      if (hasStock) {
        const s = p.stock![i];
        const pct = s !== 0 ? ((m - s) / Math.abs(s)) * 100 : 0;
        lines.push(`${t("common.stock")}: ${fmt(s)} ${p.unit ?? ""}`, `Δ ${m - s >= 0 ? "+" : ""}${fmt(m - s)} (${pct >= 0 ? "+" : ""}${pct.toFixed(1)}%)`);
      }
      return lines.join("<br/>");
    };
    const series: any[] = [{
      type: "surface", name: ov, data: pts(z), shading: "lambert",
      wireframe: { show: wire, lineStyle: { color: "rgba(0,0,0,.4)", width: 0.6 } },
      itemStyle: { opacity: 1 },
      tooltip: { formatter: (e: any) => tip(e.dataIndex) },
    }];
    if (ov === "both") series.push({
      type: "surface", name: "stock", data: pts(p.stock!), shading: "color", silent: true,
      itemStyle: { color: th.stock, opacity: 0.18 }, wireframe: { show: true, lineStyle: { color: th.stock, width: 0.8 } }, tooltip: { show: false },
    });
    const sel: number[][] = [];
    for (let i = 0; i < p.mask.length && sel.length < 4000; i++) if (p.mask[i]) sel.push([xs[i % cols], ys[Math.floor(i / cols)], z[i]]);
    series.push({ type: "scatter3D", data: sel, symbolSize: 7, itemStyle: { color: th.calc, borderColor: th.fg, borderWidth: 1 }, silent: true, tooltip: { show: false }, zlevel: -9 });
    const cam = camera.current;
    const depth = Math.max(40, Math.min(140, (100 * rows) / cols));
    return {
      animation: false,
      tooltip: { ...tooltipStyle(th) },
      visualMap: {
        show: true, type: "continuous", dimension: 2, seriesIndex: 0, right: 4, top: "middle", itemHeight: 120, itemWidth: 10,
        textStyle: { color: th.subtle, fontSize: 9 }, formatter: (v: number) => fmt(v),
        ...(ov === "delta" ? { min: -absMax, max: absMax, inRange: { color: DIVERGING } } : { min: lo, max: hi === lo ? lo + 1 : hi, inRange: { color: HEAT } }),
      },
      xAxis3D: axis3(p.xName, p.xAxis, cols),
      yAxis3D: axis3(p.yName, p.yAxis, rows),
      zAxis3D: {
        type: "value", scale: true, name: ov === "delta" ? `Δ ${p.unit ?? ""}` : p.unit, nameTextStyle: { color: th.subtle, fontSize: 10 },
        axisLabel: { color: th.subtle, fontSize: 9 }, axisLine: { lineStyle: { color: th.grid } }, splitLine: { lineStyle: { color: th.grid } }, axisPointer: { label: { show: false } },
      },
      grid3D: {
        boxWidth: 100, boxDepth: depth, boxHeight: 60,
        axisPointer: { lineStyle: { color: th.calc } },
        light: { main: { intensity: 1.15, alpha: 40, beta: 30 }, ambient: { intensity: 0.45 } },
        viewControl: {
          projection: ortho ? "orthographic" : "perspective", orthographicSize: 150, minOrthographicSize: 40, maxOrthographicSize: 400,
          alpha: cam.alpha, beta: cam.beta, distance: cam.distance, center: cam.center, minDistance: 60, maxDistance: 500,
          rotateMouseButton: "left", panMouseButton: "right", damping: 0.6, animation: false,
        },
      },
      series,
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p.values, p.stock, p.mask, p.xAxis, p.yAxis, p.unit, p.decimals, ov, wire, ortho, equal, hasStock, theme, t.lang, ready]);

  useEffect(() => { chart.current?.setOption(option as any, true); }, [option]);

  const resetView = () => {
    camera.current = { ...DEFAULT_CAMERA };
    chart.current?.setOption({ grid3D: { viewControl: { alpha: DEFAULT_CAMERA.alpha, beta: DEFAULT_CAMERA.beta, distance: DEFAULT_CAMERA.distance, center: [0, 0, 0] } } } as any);
  };

  return (
    <div className="flex h-full min-h-96 flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2 text-[11px] text-fg-muted">
        {hasStock && (
          <Segmented size="xs" value={ov} onChange={setOverlay} options={[
            { value: "mod", label: t("mapEditor.overlayMod") }, { value: "stock", label: t("mapEditor.overlayStock") },
            { value: "both", label: t("mapEditor.overlayBoth") }, { value: "delta", label: t("mapEditor.overlayDelta") },
          ]} />
        )}
        <Toggle on={wire} onClick={() => setWire((v) => !v)}>{t("mapEditor.wireframe")}</Toggle>
        <Toggle on={ortho} onClick={() => setOrtho((v) => !v)}>{t("mapEditor.orthographic")}</Toggle>
        <Toggle on={equal} disabled={forcedEqual} onClick={() => setEqual((v) => !v)} title={forcedEqual ? t("mapEditor.equalForced") : undefined}>{t("mapEditor.equalSpacing")}</Toggle>
        <Button size="xs" variant="ghost" onClick={resetView}><RotateCcw className="size-3" />{t("mapEditor.resetView")}</Button>
        <span className="ml-auto text-[10px] text-fg-subtle">{t("mapEditor.controls3d")}</span>
      </div>
      <div ref={el} className="min-h-0 flex-1" onContextMenu={(e) => e.preventDefault()}
        onPointerDownCapture={(e) => { down.current = { x: e.clientX, y: e.clientY, moved: false }; }}
        onPointerMoveCapture={(e) => { if (e.buttons && Math.hypot(e.clientX - down.current.x, e.clientY - down.current.y) > 4) down.current.moved = true; }} />
    </div>
  );
}

function Toggle({ on, onClick, children, disabled, title }: { on: boolean; onClick: () => void; children: ReactNode; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className={cn("rounded border px-1.5 py-0.5 text-[10px] transition-colors disabled:opacity-50", on ? "border-calc/40 bg-calc/15 text-fg" : "border-border text-fg-muted hover:text-fg")}>
      {children}
    </button>
  );
}
