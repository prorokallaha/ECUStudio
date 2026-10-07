"use client";
import type { ReactNode } from "react";
import { Tabs } from "@/components/ui";
import { useWorkspace } from "@/hooks/use-workspace";

/** Page-level view tabs kept in the URL (?view=…), so a view can be linked and survives reloads. */
export function ViewSwitch<T extends string>({ items, fallback, children }: { items: { value: T; label: ReactNode; icon?: ReactNode }[]; fallback: T; children: (view: T) => ReactNode }) {
  const { params, setParam } = useWorkspace();
  const raw = params.get("view") as T | null;
  const view = raw && items.some((i) => i.value === raw) ? raw : fallback;
  return (
    <div className="flex h-full min-h-0 flex-col">
      <Tabs value={view} onChange={(v) => setParam("view", v === fallback ? null : v)} items={items} className="shrink-0" />
      <div className="min-h-0 flex-1">{children(view)}</div>
    </div>
  );
}
