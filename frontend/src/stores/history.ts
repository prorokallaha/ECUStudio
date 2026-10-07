"use client";
import { create } from "zustand";

/** Undoable user edits (hardware overrides, candidate decisions, project fields). Each command knows its inverse. */
export interface Command {
  label: string;
  run: () => Promise<void>;
  undo: () => Promise<void>;
}

interface HistoryState {
  past: Command[];
  future: Command[];
  busy: boolean;
  execute: (c: Command) => Promise<void>;
  undo: () => Promise<string | null>;
  redo: () => Promise<string | null>;
  clear: () => void;
}

export const useHistory = create<HistoryState>()((set, get) => ({
  past: [],
  future: [],
  busy: false,
  async execute(c) {
    set({ busy: true });
    try {
      await c.run();
      set((s) => ({ past: [...s.past.slice(-49), c], future: [] }));
    } finally {
      set({ busy: false });
    }
  },
  async undo() {
    const { past, busy } = get();
    const c = past.at(-1);
    if (!c || busy) return null;
    set({ busy: true });
    try {
      await c.undo();
      set((s) => ({ past: s.past.slice(0, -1), future: [c, ...s.future] }));
      return c.label;
    } finally {
      set({ busy: false });
    }
  },
  async redo() {
    const { future, busy } = get();
    const c = future[0];
    if (!c || busy) return null;
    set({ busy: true });
    try {
      await c.run();
      set((s) => ({ past: [...s.past, c], future: s.future.slice(1) }));
      return c.label;
    } finally {
      set({ busy: false });
    }
  },
  clear: () => set({ past: [], future: [] }),
}));
