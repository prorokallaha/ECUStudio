"use client";
import { useMemo } from "react";
import type { EChartsOption } from "echarts";
import type { ChannelValidation } from "@/types/domain";
import { EChart } from "@/components/charts/echart";
import { axisStyle, chartTheme, tooltipStyle } from "@/components/charts/theme";
import { useUI } from "@/stores/ui";
import { useT } from "@/i18n";

/** Logged full-load medians (points) against the steady-state model (line + uncertainty band), per RPM bin. */
export function LogChannelChart({ channel, className }: { channel: ChannelValidation; className?: string }) {
  const theme = useUI((s) => s.theme);
  const tr = useT();
  const option = useMemo<EChartsOption>(() => {
    const t = chartTheme();
    const MODEL = tr("dyno.model"), LOGGED = tr("dyno.logged");
    const ax = axisStyle(t);
    const bins = channel.bins.filter((b) => b.model != null);
    const measuredColor = channel.status === "Deviates" ? t.warn : t.ok;
    return {
      animation: false,
      grid: { left: 48, right: 12, top: 26, bottom: 26 },
      legend: { top: 0, right: 0, itemWidth: 12, itemHeight: 6, textStyle: { color: t.muted, fontSize: 10 }, data: [MODEL, LOGGED] },
      tooltip: {
        trigger: "axis", ...tooltipStyle(t),
        formatter: (ps: any) => {
          const rows = (Array.isArray(ps) ? ps : [ps]).filter((p: any) => p.seriesName === MODEL || p.seriesName === LOGGED);
          const rpm = rows[0]?.value?.[0];
          const bin = channel.bins.find((b) => b.rpm === rpm);
          if (!bin) return "";
          const gap = bin.model ? ((bin.measured - bin.model) / bin.model) * 100 : null;
          return `<b>${rpm} rpm</b> · ${tr("dyno.samples", { n: bin.samples })}<br/>${LOGGED} ${bin.measured.toFixed(1)} ${channel.unit}<br/>${MODEL} ${bin.model?.toFixed(1) ?? "—"} (${bin.modelLow?.toFixed(0)}–${bin.modelHigh?.toFixed(0)})${gap != null ? `<br/>${tr("dyno.gap")} ${gap >= 0 ? "+" : ""}${gap.toFixed(1)} %` : ""}`;
        },
      },
      xAxis: { type: "value", min: "dataMin", max: "dataMax", name: "rpm", ...ax, splitLine: { show: false } },
      yAxis: { type: "value", name: channel.unit, scale: true, ...ax },
      series: [
        { name: "low", type: "line", data: bins.map((b) => [b.rpm, b.modelLow]), stack: "band", symbol: "none", lineStyle: { opacity: 0 }, silent: true, tooltip: { show: false } },
        { name: "band", type: "line", data: bins.map((b) => [b.rpm, (b.modelHigh ?? 0) - (b.modelLow ?? 0)]), stack: "band", symbol: "none", lineStyle: { opacity: 0 }, areaStyle: { color: t.calc, opacity: 0.14 }, silent: true, tooltip: { show: false } },
        { name: MODEL, type: "line", data: bins.map((b) => [b.rpm, b.model]), symbol: "none", smooth: 0.2, lineStyle: { color: t.calc, width: 2 }, itemStyle: { color: t.calc } },
        { name: LOGGED, type: "scatter", data: channel.bins.map((b) => [b.rpm, b.measured]), symbolSize: (_: any, p: any) => Math.min(10, 4 + Math.sqrt(channel.bins[p.dataIndex]?.samples ?? 1)), itemStyle: { color: measuredColor } },
      ],
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [channel, theme, tr.lang]);
  return <EChart option={option} className={className} />;
}
