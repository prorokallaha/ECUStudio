"use client";
import { ChevronDown, ChevronRight } from "lucide-react";
import { hx } from "./row-model";
import { useT } from "@/i18n";

/** One visual row standing for a whole padding run. Purely visual: the bytes and their offsets are untouched. */
export function FoldRow({ start, end, value, expanded, onToggle }: { start: number; end: number; value: number; expanded: boolean; onToggle: () => void }) {
  const t = useT();
  const count = (end - start).toLocaleString(t.lang === "ru" ? "ru-RU" : "en-US");
  const v = value.toString(16).toUpperCase().padStart(2, "0");
  return (
    <button type="button" onClick={onToggle} title={expanded ? t("editor.padding.collapse") : t("editor.padding.expand")}
      className="flex w-full items-center gap-2 rounded-sm border border-dashed border-border bg-panel-2/40 px-2 text-left text-[11px] text-fg-muted hover:bg-panel-2 hover:text-fg">
      {expanded ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
      <span className="num">{t("editor.padding.label", { value: v, count, from: hx(start), to: hx(end - 1) })}</span>
      <span className="text-fg-subtle">{expanded ? t("editor.padding.collapse") : t("editor.padding.expand")}</span>
    </button>
  );
}
