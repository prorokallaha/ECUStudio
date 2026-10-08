"use client";
import { useEffect, useRef } from "react";

type Handler = (e: KeyboardEvent) => void;

/** Keys like "mod+k", "mod+shift+z", "f". `mod` = Ctrl (Windows/Linux) or ⌘ (macOS). Plain keys are ignored inside inputs. */
export function useHotkeys(bindings: Record<string, Handler>, enabled = true) {
  const ref = useRef(bindings);
  ref.current = bindings;
  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      const typing = !!target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT" || target.isContentEditable);
      const mod = e.ctrlKey || e.metaKey;
      const combo = [mod ? "mod" : "", e.shiftKey ? "shift" : "", e.altKey ? "alt" : "", e.key.toLowerCase()].filter(Boolean).join("+");
      const h = ref.current[combo];
      if (!h) return;
      if (typing && !mod) return;
      e.preventDefault();
      h(e);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [enabled]);
}
