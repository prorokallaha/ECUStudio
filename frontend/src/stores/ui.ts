"use client";
import { create } from "zustand";
import { persist, createJSONStorage } from "zustand/middleware";

/** Per-viewer layout preferences (persisted): nav state, panels, theme, viewer defaults. */
interface UIState {
  navCollapsed: boolean;
  aiPanelOpen: boolean;
  theme: "dark" | "light";
  hexEndian: "Big" | "Little";
  hexWord: 8 | 16 | 32;
  hexSigned: boolean;
  bookmarks: Record<string, { offset: number; label: string }[]>;
  toggleNav: () => void;
  setAIPanel: (open: boolean) => void;
  setTheme: (t: "dark" | "light") => void;
  setHex: (p: Partial<Pick<UIState, "hexEndian" | "hexWord" | "hexSigned">>) => void;
  addBookmark: (sha: string, offset: number, label: string) => void;
  removeBookmark: (sha: string, offset: number) => void;
}

const safeStorage = createJSONStorage(() => {
  try { return window.localStorage; } catch { return { getItem: () => null, setItem: () => {}, removeItem: () => {} }; }
});

export const useUI = create<UIState>()(
  persist(
    (set) => ({
      navCollapsed: false,
      aiPanelOpen: true,
      theme: "dark",
      hexEndian: "Big",
      hexWord: 16,
      hexSigned: false,
      bookmarks: {},
      toggleNav: () => set((s) => ({ navCollapsed: !s.navCollapsed })),
      setAIPanel: (open) => set({ aiPanelOpen: open }),
      setTheme: (theme) => set({ theme }),
      setHex: (p) => set(p),
      addBookmark: (sha, offset, label) => set((s) => ({ bookmarks: { ...s.bookmarks, [sha]: [...(s.bookmarks[sha] ?? []).filter((b) => b.offset !== offset), { offset, label }].sort((a, b) => a.offset - b.offset) } })),
      removeBookmark: (sha, offset) => set((s) => ({ bookmarks: { ...s.bookmarks, [sha]: (s.bookmarks[sha] ?? []).filter((b) => b.offset !== offset) } })),
    }),
    { name: "ecustudio.ui", storage: safeStorage },
  ),
);
