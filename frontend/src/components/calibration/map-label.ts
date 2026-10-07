import type { MapCandidate, MapSummary, SourceType } from "@/types/domain";
import type { useT } from "@/i18n";

type T = ReturnType<typeof useT>;

/** Sources that only produce hypotheses: a map from them is never shown under its hypothesised name. */
const WEAK_SOURCES: SourceType[] = ["SignatureScan", "AIInferred", "Assumption", "Unknown"];
const WEAK_CONFIDENCE = 0.5;

export function isWeakMap(m: MapSummary): boolean {
  return WEAK_SOURCES.includes(m.source) || m.confidence < WEAK_CONFIDENCE;
}

const pct = (x: number) => Math.round(x * 100);

/** "Неизвестная карта · Кандидат: <роль>, 18%" — the hypothesis is only ever a qualifier, never the name. */
export function unknownLabel(t: T, role?: string | null, confidence?: number | null): string {
  const base = t("mapEditor.unknownMap");
  if (!role || role === "Unknown") return base;
  return `${base} · ${t("mapEditor.candidateHyp", { role: t.tx(`role.${role}`, role), pct: pct(confidence ?? 0) })}`;
}

/**
 * Display name of an identified map. Strong (definition / user) maps show their role in the UI language when the role
 * is unique in the report, else the definition name; weak maps show the "unknown map" label.
 */
export function mapLabel(t: T, m: MapSummary, roleCount?: Map<string, number>): string {
  if (isWeakMap(m)) return unknownLabel(t, m.role, m.confidence);
  if (m.role !== "Unknown" && (roleCount?.get(m.role) ?? 1) === 1) return t.tx(`role.${m.role}`, m.name);
  return m.name;
}

/** Candidate label: a user-confirmed role is a decision, not a hypothesis; anything else stays "unknown". */
export function candidateLabel(t: T, c: MapCandidate): string {
  if (c.status === "Confirmed" && c.confirmedRole) return t.tx(`role.${c.confirmedRole}`, c.confirmedRole);
  if (c.status === "Rejected") return t("mapEditor.unknownMap");
  return unknownLabel(t, c.best?.role, c.best?.confidence);
}

export function roleCounts(maps: MapSummary[]): Map<string, number> {
  const m = new Map<string, number>();
  for (const x of maps) if (!isWeakMap(x)) m.set(x.role, (m.get(x.role) ?? 0) + 1);
  return m;
}
