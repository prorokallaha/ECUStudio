"use client";
import { useMemo } from "react";
import type { EChartsOption } from "echarts";
import type { DynoResult, Estimate, PointResult } from "@/types/domain";
import { EChart } from "@/components/charts/echart";
import { axisStyle, chartTheme, tooltipStyle } from "@/components/charts/theme";
import { useUI } from "@/stores/ui";

type Pick = (p: PointResult) => Estimate | undefined;

function seriesWithBand(points: PointResult[], pick: Pick, name: string, color: string, opts: { yAxisIndex?: number; dashed?: boolean; band?: boolean } = {}) {
  const ok = points.filter((p) => pick(p)?.value !== undefined);
  const out: any[] = [];
  if (opts.band !== false) {
    out.push({ name: `${name}·lo`, type: "line", data: ok.map((p) => [p.point.rpm, pick(p)!.low]), lineStyle: { opacity: 0 }, symbol: "none", stack: `${name}-band`, yAxisIndex: opts.yAxisIndex ?? 0, silent: true, tooltip: { show: false } });
    out.push({ name: `${name}·band`, type: "line", data: ok.map((p) => [p.point.rpm, (pick(p)!.high ?? 0) - (pick(p)!.low ?? 0)]), lineStyle: { opacity: 0 }, symbol: "none", stack: `${name}-band`, areaStyle: { color, opacity: 0.13 }, yAxisIndex: opts.yAxisIndex ?? 0, silent: true, tooltip: { show: false } });
  }
  out.push({
    name, type: "line", smooth: 0.2, symbol: "circle", symbolSize: 3, showSymbol: false, yAxisIndex: opts.yAxisIndex ?? 0,
    data: ok.map((p) => ({ value: [p.point.rpm, pick(p)!.value], beyond: p.beyondCalibratedRange })),
    lineStyle: { color, width: opts.dashed ? 1.5 : 2.2, type: opts.dashed ? "dashed" : "solid" }, itemStyle: { color },
  });
  return out;
}

/** Main Virtual Dyno chart: torque + power, stock vs modified, each with its uncertainty envelope. */
export function DynoMainChart({ d, onPick, group }: { d: DynoResult; onPick: (rpm: number) => void; group: string }) {
  const theme = useUI((s) => s.theme);
  const option = useMemo<EChartsOption>(() => {
    const t = chartTheme();
    const ax = axisStyle(t);
    const series = [
      ...seriesWithBand(d.modified.points, (p) => p.torque, "Torque · mod", t.mod),
      ...seriesWithBand(d.modified.points, (p) => p.powerHp, "Power · mod", t.warn, { yAxisIndex: 1 }),
      ...(d.stock ? [
        ...seriesWithBand(d.stock.points, (p) => p.torque, "Torque · stock", t.stock, { dashed: true }),
        ...seriesWithBand(d.stock.points, (p) => p.powerHp, "Power · stock", t.stock, { yAxisIndex: 1, dashed: true, band: false }),
      ] : []),
    ];
    const beyond = d.modified.points.filter((p) => p.beyondCalibratedRange).map((p) => p.point.rpm);
    return {
      animation: false,
      grid: { left: 52, right: 52, top: 30, bottom: 26 },
      legend: { top: 0, data: ["Torque · mod", "Power · mod", "Torque · stock", "Power · stock"], textStyle: { color: t.muted, fontSize: 11 }, itemWidth: 14, itemHeight: 2 },
      tooltip: { trigger: "axis", ...tooltipStyle(t), axisPointer: { type: "line", lineStyle: { color: t.calc } },
        formatter: (ps: any) => {
          const arr = (Array.isArray(ps) ? ps : [ps]).filter((p: any) => !String(p.seriesName).includes("·lo") && !String(p.seriesName).includes("·band"));
          const rpm = arr[0]?.value?.[0] ?? arr[0]?.data?.value?.[0];
          return `<b>${rpm} rpm</b><br/>` + arr.map((p: any) => `${p.marker}${p.seriesName}: <b>${Number((p.data?.value ?? p.value)[1]).toFixed(0)}</b>`).join("<br/>") + "<br/><span style='opacity:.6'>click for calculation trace</span>";
        } },
      xAxis: { type: "value", min: d.request.rpmStart, max: d.request.rpmEnd, ...ax, splitLine: { show: false } },
      yAxis: [{ type: "value", name: "Nm", ...ax }, { type: "value", name: "hp", ...ax, splitLine: { show: false } }],
      series: [
        ...series,
        ...(beyond.length ? [{ type: "line", name: "beyond", data: [], markArea: { silent: true, itemStyle: { color: t.unknown, opacity: 0.08 }, label: { show: true, color: t.subtle, fontSize: 10, position: "insideTop", formatter: "beyond calibrated range" }, data: [[{ xAxis: Math.min(...beyond) }, { xAxis: d.request.rpmEnd }]] } }] : []),
      ] as any,
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d, theme]);
  return <EChart option={option} group={group} onAxisClick={onPick} />;
}

/** Small synced chart (boost / IQ / λ / EGT) sharing the axis pointer with the main chart. */
export function DynoAuxChart({ d, pick, title, unit, group, onPick, refLine }: { d: DynoResult; pick: Pick; title: string; unit: string; group: string; onPick: (rpm: number) => void; refLine?: { value: number; label: string } }) {
  const theme = useUI((s) => s.theme);
  const option = useMemo<EChartsOption>(() => {
    const t = chartTheme();
    const ax = axisStyle(t);
    const mod = seriesWithBand(d.modified.points, pick, `${title} mod`, t.mod);
    if (refLine) mod[mod.length - 1].markLine = { silent: true, symbol: "none", lineStyle: { color: t.warn, type: "dashed" }, label: { color: t.warn, fontSize: 9, formatter: refLine.label }, data: [{ yAxis: refLine.value }] };
    return {
      animation: false,
      title: { text: `${title} [${unit}]`, left: 4, top: 0, textStyle: { color: t.muted, fontSize: 11, fontWeight: 500 } },
      grid: { left: 48, right: 12, top: 22, bottom: 20 },
      tooltip: { trigger: "axis", ...tooltipStyle(t), valueFormatter: (v: any) => (typeof v === "number" ? v.toFixed(unit === "-" ? 2 : 0) : v) },
      xAxis: { type: "value", min: d.request.rpmStart, max: d.request.rpmEnd, ...ax, splitLine: { show: false } },
      yAxis: { type: "value", scale: true, ...ax },
      series: [...mod, ...(d.stock ? seriesWithBand(d.stock.points, pick, `${title} stock`, t.stock, { dashed: true, band: false }) : [])] as any,
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d, theme, pick, title, unit, refLine]);
  return <EChart option={option} group={group} onAxisClick={onPick} />;
}
