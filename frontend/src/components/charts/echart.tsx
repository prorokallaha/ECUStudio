"use client";
import { useEffect, useRef } from "react";
import type { ECharts, EChartsOption } from "echarts";
import { useUI } from "@/stores/ui";
import { cn } from "@/lib/cn";

type Loader = () => Promise<typeof import("echarts")>;
const loadCore: Loader = () => import("echarts");
const loadGl: Loader = async () => { const e = await import("echarts"); await import("echarts-gl"); return e; };

/**
 * Thin ECharts wrapper: lazy-loaded chunk, resize observer, theme-aware re-render, optional group for synced
 * tooltips/axes across charts (echarts.connect).
 */
export function EChart({ option, className, gl = false, group, onEvents, notMerge = true, onAxisClick }: {
  option: EChartsOption; className?: string; gl?: boolean; group?: string;
  onEvents?: Record<string, (params: any, chart: ECharts) => void>; notMerge?: boolean;
  /** Click anywhere in the plot area → x value on the first x axis. */
  onAxisClick?: (x: number) => void;
}) {
  const el = useRef<HTMLDivElement>(null);
  const chart = useRef<ECharts | null>(null);
  const theme = useUI((s) => s.theme);
  const events = useRef(onEvents);
  events.current = onEvents;
  const axisClick = useRef(onAxisClick);
  axisClick.current = onAxisClick;

  useEffect(() => {
    let disposed = false;
    let ro: ResizeObserver | undefined;
    (gl ? loadGl : loadCore)().then((echarts) => {
      if (disposed || !el.current) return;
      chart.current = echarts.init(el.current, undefined, { renderer: "canvas" });
      if (group) { chart.current.group = group; echarts.connect(group); }
      chart.current.setOption(option, true);
      for (const name of Object.keys(events.current ?? {})) chart.current.on(name, (p: unknown) => events.current?.[name]?.(p, chart.current!));
      chart.current.getZr().on("click", (e: { offsetX: number; offsetY: number }) => {
        const c = chart.current;
        if (!c || !axisClick.current || !c.containPixel({ gridIndex: 0 }, [e.offsetX, e.offsetY])) return;
        const v = c.convertFromPixel({ xAxisIndex: 0 }, e.offsetX) as unknown as number;
        if (Number.isFinite(v)) axisClick.current(v);
      });
      ro = new ResizeObserver(() => chart.current?.resize());
      ro.observe(el.current);
    });
    return () => { disposed = true; ro?.disconnect(); chart.current?.dispose(); chart.current = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gl, group, theme]);

  useEffect(() => { chart.current?.setOption(option, notMerge); }, [option, notMerge]);

  return <div ref={el} className={cn("h-full w-full", className)} />;
}
