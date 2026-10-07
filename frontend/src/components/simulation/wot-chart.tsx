"use client";
import { useMemo } from "react";
import type { EChartsOption } from "echarts";
import type { WotSample } from "@/types/domain";
import { EChart } from "@/components/charts/echart";
import { axisStyle, chartTheme, tooltipStyle } from "@/components/charts/theme";
import { useUI } from "@/stores/ui";

/** Compact torque/power chart with confidence envelopes, from the report's WOT samples. */
export function WotChart({ mod, stock, className }: { mod: WotSample[]; stock?: WotSample[] | null; className?: string }) {
  const theme = useUI((s) => s.theme);
  const option = useMemo<EChartsOption>(() => {
    const t = chartTheme();
    const ax = axisStyle(t);
    const band = (rows: WotSample[], pick: (w: WotSample) => { low?: number; high?: number }, color: string, name: string, yAxisIndex: number) => [
      { name: `${name} low`, type: "line" as const, data: rows.map((w) => [w.rpm, pick(w).low ?? null]), lineStyle: { opacity: 0 }, symbol: "none", stack: name, yAxisIndex, silent: true, tooltip: { show: false } },
      { name: `${name} band`, type: "line" as const, data: rows.map((w) => [w.rpm, (pick(w).high ?? 0) - (pick(w).low ?? 0)]), lineStyle: { opacity: 0 }, symbol: "none", stack: name, areaStyle: { color, opacity: 0.14 }, yAxisIndex, silent: true, tooltip: { show: false } },
    ];
    const line = (rows: WotSample[], pick: (w: WotSample) => number | undefined, name: string, color: string, yAxisIndex: number, dashed = false) => ({
      name, type: "line" as const, data: rows.map((w) => [w.rpm, pick(w) ?? null]), symbol: "none", yAxisIndex, smooth: 0.25,
      lineStyle: { color, width: dashed ? 1.5 : 2, type: dashed ? ("dashed" as const) : ("solid" as const) }, itemStyle: { color },
    });
    const series: any[] = [
      ...band(mod, (w) => w.torqueNm, t.mod, "tq-mod", 0),
      line(mod, (w) => w.torqueNm.value, "Torque mod", t.mod, 0),
      line(mod, (w) => w.powerHp.value, "Power mod", t.warn, 1),
    ];
    if (stock?.length) {
      series.push(line(stock, (w) => w.torqueNm.value, "Torque stock", t.stock, 0, true));
      series.push(line(stock, (w) => w.powerHp.value, "Power stock", t.stock, 1, true));
    }
    return {
      animation: false,
      grid: { left: 44, right: 44, top: 24, bottom: 28 },
      legend: { top: 0, right: 0, itemWidth: 12, itemHeight: 2, textStyle: { color: t.muted, fontSize: 10 }, data: ["Torque mod", "Power mod", "Torque stock", "Power stock"] },
      tooltip: { trigger: "axis", ...tooltipStyle(t), valueFormatter: (v: any) => (typeof v === "number" ? v.toFixed(0) : v) },
      xAxis: { type: "value", min: "dataMin", max: "dataMax", name: "rpm", nameLocation: "end", ...ax, splitLine: { show: false } },
      yAxis: [{ type: "value", name: "Nm", ...ax }, { type: "value", name: "hp", ...ax, splitLine: { show: false } }],
      series,
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mod, stock, theme]);
  return <EChart option={option} className={className} />;
}
