"use client";
import { create } from "zustand";
import { currentLang, translate } from "@/i18n";

/**
 * What the engineer is looking at right now. The AI panel attaches exactly this (plus the analysis id),
 * never the whole project or the binary.
 */
export type Selection =
  | { kind: "map"; mapId: string; mapName: string; cells?: { row: number; col: number; x: number; y: number; stock?: number | null; mod: number }[]; unit?: string }
  | { kind: "point"; rpm: number; pedalPct: number; gear: number; label?: string }
  | { kind: "component"; component: string; label: string }
  | { kind: "finding"; code: string; text: string }
  | { kind: "candidate"; candidateId: string }
  | { kind: "hex"; offset: number; length: number };

interface SelectionState {
  selection: Selection | null;
  select: (s: Selection | null) => void;
}

export const useSelection = create<SelectionState>()((set) => ({
  selection: null,
  select: (selection) => set({ selection }),
}));

/** Human-readable description of the selection in the current UI language (shown in the AI panel, not sent to the server). */
export function describeSelection(s: Selection | null): string {
  const lang = currentLang();
  if (!s) return translate(lang, "selection.whole");
  switch (s.kind) {
    case "map": return s.cells?.length ? translate(lang, "selection.map", { name: s.mapName, n: s.cells.length }) : s.mapName;
    case "point": return translate(lang, "selection.point", { rpm: s.rpm, pedal: s.pedalPct, gear: s.gear });
    case "component": return translate(lang, "selection.component", { label: s.label });
    case "finding": return translate(lang, "selection.finding", { code: s.code });
    case "candidate": return translate(lang, "selection.candidate", { id: s.candidateId });
    case "hex": return translate(lang, "selection.hex", { addr: `0x${s.offset.toString(16).toUpperCase()}`, len: s.length });
  }
}
