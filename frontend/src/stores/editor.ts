"use client";
import { create } from "zustand";
import { persist, createJSONStorage } from "zustand/middleware";
import type { ByteOrder, NumBase, WordKind } from "@/types/editing";

/**
 * Hex editor / byte diff display preferences (persisted). Purely visual: none of these values change the data,
 * none are sent to the server and none are saved into a file.
 */
interface EditorPrefsState {
  hidePadding: boolean;
  word: WordKind;
  base: NumBase;
  signed: boolean;
  order: ByteOrder;
  showAscii: boolean;
  set: (p: Partial<Pick<EditorPrefsState, "hidePadding" | "word" | "base" | "signed" | "order" | "showAscii">>) => void;
}

const safeStorage = createJSONStorage(() => {
  try { return window.localStorage; } catch { return { getItem: () => null, setItem: () => {}, removeItem: () => {} }; }
});

export const useEditorPrefs = create<EditorPrefsState>()(
  persist(
    (set) => ({
      hidePadding: true,
      word: "8",
      base: "hex",
      signed: false,
      order: "Big",
      showAscii: true,
      set: (p) => set(p),
    }),
    { name: "ecustudio.editor", storage: safeStorage },
  ),
);
