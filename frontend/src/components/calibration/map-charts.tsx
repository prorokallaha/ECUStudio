"use client";
import { useMemo } from "react";
import type { EChartsOption } from "echarts";
import { EChart } from "@/components/charts/echart";
import { axisStyle, chartTheme, tooltipStyle } from "@/components/charts/theme";
import { useUI } from "@/stores/ui";
import type { CellRef } from "./map-grid";

interface Data { xAxis: number[]; yAxis: number[]; values: number[]; stock?: number[] | null; unit?: string; xName?: string; yName?: string }

const at = (d: Data, r: number, c: number, src: number[]) => src[r * d.xAxis.length + c];

/** 2D: one line per Y breakpoint (stock dashed). Selection highlights the row of the selected cell. */
export function Map2D({ d, selected, onPick }: { d: Data; selected: CellRef[]; onPick: (c: CellRef) => void }) {
  const theme = useUI((s) => s.theme);
  const option = useMemo<EChartsOption>(() => {
    const t = chartTheme();
    const ax = axisStyle(t);
    const selRow = selected[0]?.row;
    const palette = [t.calc, t.ok, t.attn, t.warn, t.ai, t.danger];
    const series: any[] = [];
    d.yAxis.forEach((y, r) => {
      const color = palette[r % palette.length];
      const emph = selRow === undefined || selRow === r;
      series.push({ name: `${y}`, type: "line", symbolSize: 5, data: d.xAxis.map((x, c) => [x, at(d, r, c, d.values)]), lineStyle: { color, width: emph ? 2 : 1, opacity: emph ? 1 : 0.35 }, itemStyle: { color, opacity: emph ? 1 : 0.35 } });
      if (d.stock) series.push({ name: `${y} stock`, type: "line", symbol: "none", data: d.xAxis.map((x, c) => [x, at(d, r, c, d.stock!)]), lineStyle: { color, width: 1, type: "dashed", opacity: emph ? 0.7 : 0.2 }, tooltip: { show: false } });
    });
    return {
      animation: false,
      grid: { left: 52, right: 16, top: 28, bottom: 32 },
      legend: { type: "scroll", top: 0, textStyle: { color: t.muted, fontSize: 10 }, data: d.yAxis.map(String), itemWidth: 10, itemHeight: 2 },
      tooltip: { trigger: "axis", ...tooltipStyle(t) },
      xAxis: { type: "value", name: d.xName, min: "dataMin", max: "dataMax", ...ax },
      yAxis: { type: "value", name: d.unit, scale: true, ...ax },
      series,
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d, selected, theme]);
  return <EChart option={option} onEvents={{ click: (p: any) => { const r = d.yAxis.findIndex((y) => String(y) === String(p.seriesName)); if (r >= 0) onPick({ row: r, col: p.dataIndex }); } }} />;
}

/** Heatmap of values or of % delta vs stock. */
export function MapHeatmap({ d, delta, onPick }: { d: Data; delta?: boolean; onPick: (c: CellRef) => void }) {
  const theme = useUI((s) => s.theme);
  const option = useMemo<EChartsOption>(() => {
    const t = chartTheme();
    const ax = axisStyle(t);
    const data: [number, number, number][] = [];
    let lo = Infinity, hi = -Infinity;
    d.yAxis.forEach((_, r) => d.xAxis.forEach((_, c) => {
      const m = at(d, r, c, d.values);
      const s = d.stock ? at(d, r, c, d.stock) : m;
      const v = delta ? (s ? ((m - s) / Math.abs(s)) * 100 : 0) : m;
      lo = Math.min(lo, v); hi = Math.max(hi, v);
      data.push([c, r, Math.round(v * 100) / 100]);
    }));
    const absMax = Math.max(Math.abs(lo), Math.abs(hi)) || 1;
    return {
      animation: false,
      grid: { left: 56, right: 70, top: 10, bottom: 36 },
      tooltip: { ...tooltipStyle(t), formatter: (p: any) => `${d.xName ?? "X"} ${d.xAxis[p.value[0]]} · ${d.yName ?? "Y"} ${d.yAxis[p.value[1]]}<br/><b>${p.value[2]}${delta ? " %" : ` ${d.unit ?? ""}`}</b>` },
      xAxis: { type: "category", data: d.xAxis.map(String), name: d.xName, ...ax, splitArea: { show: false } },
      yAxis: { type: "category", data: d.yAxis.map(String), name: d.yName, ...ax },
      visualMap: delta
        ? { min: -absMax, max: absMax, calculable: true, orient: "vertical", right: 0, top: "middle", textStyle: { color: t.subtle, fontSize: 10 }, inRange: { color: [t.calc, t.panel, t.warn, t.danger] } }
        : { min: lo, max: hi, calculable: true, orient: "vertical", right: 0, top: "middle", textStyle: { color: t.subtle, fontSize: 10 }, inRange: { color: ["#0b3d91", "#1f77b4", "#2ca02c", "#ffbf00", "#ff7f0e", "#d62728"] } },
      series: [{ type: "heatmap", data, label: { show: d.xAxis.length <= 16, fontSize: 9, color: t.fg, formatter: (p: any) => String(p.value[2]) }, emphasis: { itemStyle: { borderColor: t.fg, borderWidth: 1 } } }],
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d, delta, theme]);
  return <EChart option={option} onEvents={{ click: (p: any) => onPick({ row: p.value[1], col: p.value[0] }) }} />;
}

/** 3D surface (echarts-gl, lazy-loaded). Stock as a translucent wireframe when available. */
export function Map3D({ d }: { d: Data }) {
  const theme = useUI((s) => s.theme);
  const option = useMemo<EChartsOption>(() => {
    const t = chartTheme();
    const pts = (src: number[]) => d.yAxis.flatMap((y, r) => d.xAxis.map((x, c) => [x, y, at(d, r, c, src)]));
    const vals = d.values;
    const axis3 = (name?: string) => ({ name, nameTextStyle: { color: t.subtle }, axisLabel: { color: t.subtle, fontSize: 9 }, axisLine: { lineStyle: { color: t.grid } }, splitLine: { lineStyle: { color: t.grid } } });
    const series: any[] = [{ type: "surface", name: "Modified", data: pts(vals), shading: "color", wireframe: { show: true, lineStyle: { color: "rgba(0,0,0,.35)", width: 0.5 } } }];
    if (d.stock) series.push({ type: "surface", name: "Stock", data: pts(d.stock), itemStyle: { color: t.stock, opacity: 0.25 }, wireframe: { show: true, lineStyle: { color: t.stock, width: 0.6 } } });
    return {
      tooltip: {},
      visualMap: { show: false, dimension: 2, min: Math.min(...vals), max: Math.max(...vals), inRange: { color: ["#1f4e9c", "#1f77b4", "#2ca02c", "#ffbf00", "#ff7f0e", "#d62728"] }, seriesIndex: 0 },
      xAxis3D: { type: "value", ...axis3(d.xName) },
      yAxis3D: { type: "value", ...axis3(d.yName) },
      zAxis3D: { type: "value", ...axis3(d.unit) },
      grid3D: { boxWidth: 120, boxDepth: 80, viewControl: { distance: 210, alpha: 25, beta: 40 }, axisPointer: { lineStyle: { color: t.calc } }, light: { main: { intensity: 1.1 } } },
      series,
    } as EChartsOption;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d, theme]);
  return <EChart option={option} gl />;
}
