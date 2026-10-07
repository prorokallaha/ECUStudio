"use client";
import { create } from "zustand";

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

export function describeSelection(s: Selection | null): string {
  if (!s) return "Whole analysis (summary context)";
  switch (s.kind) {
    case "map": return s.cells?.length ? `${s.mapName} · ${s.cells.length} cell(s)` : s.mapName;
    case "point": return `Operating point ${s.rpm} rpm · ${s.pedalPct}% pedal · gear ${s.gear}`;
    case "component": return `Component: ${s.label}`;
    case "finding": return `Finding ${s.code}`;
    case "candidate": return `Unknown map ${s.candidateId}`;
    case "hex": return `Hex 0x${s.offset.toString(16).toUpperCase()} (+${s.length})`;
  }
}
