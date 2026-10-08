import { cssVar } from "@/lib/colors";

/** Shared chart styling resolved from CSS tokens at render time (dark/light aware). */
export function chartTheme() {
  return {
    fg: cssVar("fg"),
    muted: cssVar("fg-muted"),
    subtle: cssVar("fg-subtle"),
    grid: cssVar("border", 0.6),
    panel: cssVar("bg-elev"),
    stock: cssVar("stock"),
    mod: cssVar("mod"),
    ok: cssVar("ok"), attn: cssVar("attn"), warn: cssVar("warn"), danger: cssVar("danger"), ai: cssVar("ai"), unknown: cssVar("unknown"),
    calc: cssVar("calc"),
  };
}

export function axisStyle(t = chartTheme()) {
  return {
    axisLine: { lineStyle: { color: t.grid } },
    axisTick: { lineStyle: { color: t.grid } },
    axisLabel: { color: t.subtle, fontSize: 10, fontFamily: "JetBrains Mono, monospace", hideOverlap: true },
    splitLine: { lineStyle: { color: t.grid, type: "dashed" as const } },
    nameTextStyle: { color: t.subtle, fontSize: 10 },
  };
}

export function tooltipStyle(t = chartTheme()) {
  return {
    backgroundColor: t.panel,
    borderColor: t.grid,
    textStyle: { color: t.fg, fontSize: 11 },
    extraCssText: "box-shadow: 0 8px 24px rgba(0,0,0,.35); border-radius: 6px;",
  };
}

/** Diverging colour scale for deltas: blue = decrease, neutral = 0, orange/red = increase. */
export function deltaColor(pct: number): string {
  const a = Math.min(1, Math.abs(pct) / 30);
  if (Math.abs(pct) < 0.05) return "transparent";
  return pct > 0 ? cssVar(pct > 20 ? "danger" : "warn", 0.15 + a * 0.55) : cssVar("calc", 0.15 + a * 0.55);
}
