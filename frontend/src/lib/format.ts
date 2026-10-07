import type { ConfidenceLevel, Estimate, Param, Severity, SourceType } from "@/types/domain";

/** Mirrors ECUStudio.Core.EngineeringRounding: values are shown at the resolution the model actually has. */
const STEPS: Record<string, number> = {
  hp: 1, kW: 1, Nm: 1, mbar: 10, "mg/stroke": 0.5, mg: 0.5, "°C": 5, K: 5, "%": 1, rpm: 10,
  "kg/s": 0.001, "-": 0.01, "°CA": 0.1, "°BTDC": 0.1, bar: 1, "g/s": 1,
};

export function engRound(value: number, unit = "-"): number {
  const step = STEPS[unit];
  if (!step) return Math.round(value * 1000) / 1000;
  return Math.round(Math.round(value / step) * step * 1e6) / 1e6;
}

export function fmtNumber(value: number | null | undefined, unit = "-"): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "—";
  const r = engRound(value, unit);
  const step = STEPS[unit] ?? 0.001;
  const decimals = step >= 1 ? 0 : Math.min(3, Math.max(0, Math.ceil(-Math.log10(step))));
  return r.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: decimals }).replace(/,/g, " ");
}

export const unitLabel = (unit?: string) => (!unit || unit === "-" ? "" : unit);

export function isKnown(e?: Estimate | null): e is Estimate & { value: number } {
  return !!e && e.value !== undefined && e.value !== null && e.kind !== "Unknown" && e.kind !== "NotApplicable";
}

export interface EstimateText { value: string; range: string | null; unit: string; state: "known" | "unknown" | "na" }

export function fmtEstimate(e?: Estimate | null): EstimateText {
  if (!e) return { value: "—", range: null, unit: "", state: "unknown" };
  if (e.kind === "NotApplicable") return { value: "N/A", range: null, unit: "", state: "na" };
  if (!isKnown(e)) return { value: "UNKNOWN", range: null, unit: unitLabel(e.unit), state: "unknown" };
  const u = e.unit ?? "-";
  const lo = e.low ?? e.value, hi = e.high ?? e.value;
  const range = engRound(lo, u) === engRound(hi, u) ? null : `${fmtNumber(lo, u)}–${fmtNumber(hi, u)}`;
  return { value: fmtNumber(e.value, u), range, unit: unitLabel(u), state: "known" };
}

export function estimateString(e?: Estimate | null): string {
  const t = fmtEstimate(e);
  if (t.state !== "known") return t.value;
  return `${t.value}${t.unit ? " " + t.unit : ""}${t.range ? ` (${t.range})` : ""}`;
}

export function fmtParam(p?: Param | null): string {
  if (!p || p.source === "Unknown" || (p.number === undefined && !p.text)) return "UNKNOWN";
  if (p.text) return p.text;
  return `${fmtNumber(p.number, p.unit)}${p.unit && p.unit !== "-" ? " " + p.unit : ""}`;
}

export function confidenceLevel(score?: number | null): ConfidenceLevel {
  const s = score ?? 0;
  if (s <= 0) return "Unknown";
  if (s < 0.35) return "Low";
  if (s < 0.5) return "LowMedium";
  if (s < 0.65) return "Medium";
  if (s < 0.8) return "MediumHigh";
  return "High";
}

/** UI collapses the six backend levels to the four badge levels from the spec. */
export function confidenceBadge(score?: number | null): "HIGH" | "MEDIUM" | "LOW" | "UNKNOWN" {
  const l = confidenceLevel(score);
  if (l === "High" || l === "MediumHigh") return "HIGH";
  if (l === "Medium" || l === "LowMedium") return l === "Medium" ? "MEDIUM" : "LOW";
  if (l === "Low") return "LOW";
  return "UNKNOWN";
}

export const SEVERITY_ORDER: Severity[] = ["Safe", "Review", "Unknown", "Warning", "Danger"];
export const severityRank = (s?: Severity | null) => (s ? SEVERITY_ORDER.indexOf(s) : -1);

export const severityLabel: Record<Severity, string> = {
  Safe: "SAFE", Review: "REVIEW", Warning: "WARNING", Danger: "DANGER", Unknown: "UNKNOWN",
};

/** Spec badge families: OEM, DAMOS, A2L, Database, Calculated, AI inferred, User entered (+ scan/xdf/log/unknown). */
export const SOURCE_META: Record<SourceType, { label: string; family: "oem" | "damos" | "a2l" | "db" | "calc" | "ai" | "user" | "scan" | "log" | "unknown" }> = {
  OemSpec: { label: "OEM", family: "oem" },
  PublicSpec: { label: "Public spec", family: "oem" },
  Database: { label: "Database", family: "db" },
  VariantTypical: { label: "Database · typical", family: "db" },
  VinDecode: { label: "VIN decode", family: "db" },
  EcuBinary: { label: "ECU binary", family: "oem" },
  FileName: { label: "File name", family: "unknown" },
  Damos: { label: "DAMOS", family: "damos" },
  A2L: { label: "A2L", family: "a2l" },
  Xdf: { label: "XDF", family: "a2l" },
  DefinitionDb: { label: "Definition DB", family: "db" },
  SignatureScan: { label: "Signature scan", family: "scan" },
  DiagnosticLog: { label: "Diagnostic log", family: "log" },
  Calculated: { label: "Calculated", family: "calc" },
  AIInferred: { label: "AI inferred", family: "ai" },
  User: { label: "User entered", family: "user" },
  Assumption: { label: "Assumption", family: "unknown" },
  Unknown: { label: "Unknown", family: "unknown" },
};

export const hex = (n: number, pad = 6) => "0x" + n.toString(16).toUpperCase().padStart(pad, "0");

export function fmtBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  if (n < 1024 ** 3) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  if (n < 1024 ** 4) return `${(n / 1024 ** 3).toFixed(1)} GB`;
  return `${(n / 1024 ** 4).toFixed(2)} TB`;
}

export function fmtDate(iso?: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return d.toLocaleString(undefined, { year: "numeric", month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit" });
}

export const pct = (v?: number | null, digits = 0) =>
  v === undefined || v === null || !Number.isFinite(v) ? "—" : `${v > 0 ? "+" : ""}${v.toFixed(digits)}%`;
