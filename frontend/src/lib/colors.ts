import type { NodeState, Severity } from "@/types/domain";

/** Functional palette — the same meaning in badges, charts, graph nodes and bars. */
export const severityTone: Record<Severity, "ok" | "attn" | "warn" | "danger" | "unknown"> = {
  Safe: "ok", Review: "attn", Warning: "warn", Danger: "danger", Unknown: "unknown",
};

export const toneText = { ok: "text-ok", attn: "text-attn", warn: "text-warn", danger: "text-danger", unknown: "text-unknown", calc: "text-calc", ai: "text-ai" } as const;
export const toneBg = { ok: "bg-ok", attn: "bg-attn", warn: "bg-warn", danger: "bg-danger", unknown: "bg-unknown", calc: "bg-calc", ai: "bg-ai" } as const;
export const toneSoft = {
  ok: "bg-ok/12 text-ok border-ok/30",
  attn: "bg-attn/12 text-attn border-attn/30",
  warn: "bg-warn/12 text-warn border-warn/30",
  danger: "bg-danger/12 text-danger border-danger/35",
  unknown: "bg-unknown/12 text-unknown border-unknown/30",
  calc: "bg-calc/12 text-calc border-calc/30",
  ai: "bg-ai/12 text-ai border-ai/30",
} as const;
export type Tone = keyof typeof toneSoft;

export const nodeStateTone: Record<NodeState, Tone> = {
  Stock: "ok", Modified: "calc", Warning: "warn", Danger: "danger", Unknown: "unknown", NotApplicable: "unknown",
};

/** Resolved CSS colours for canvas-based charts (ECharts cannot read Tailwind classes). */
export function cssVar(name: string, alpha = 1): string {
  if (typeof window === "undefined") return "#888";
  const v = getComputedStyle(document.documentElement).getPropertyValue(`--${name}`).trim();
  return v ? `hsl(${v} / ${alpha})` : "#888";
}
