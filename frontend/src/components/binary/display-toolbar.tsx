"use client";
import { Segmented } from "@/components/ui";
import { useEditorPrefs } from "@/stores/editor";
import type { WordKind } from "@/types/editing";
import { useT } from "@/i18n";

/** Display interpretation controls (persisted). They only change how bytes are shown, never the bytes. */
export function DisplayToolbar({ ascii = true, words = true }: { ascii?: boolean; words?: boolean }) {
  const t = useT();
  const p = useEditorPrefs();
  return (
    <div className="flex flex-wrap items-center gap-2" title={t("editor.displayOnly")}>
      {words && <Segmented value={p.word} onChange={(v: WordKind) => p.set({ word: v })} options={[
        { value: "8", label: t("binary.bit8") }, { value: "16", label: t("binary.bit16") }, { value: "32", label: t("binary.bit32") }, { value: "f32", label: "float32" },
      ]} />}
      <Segmented value={p.base} onChange={(v) => p.set({ base: v })} options={[{ value: "hex", label: "HEX" }, { value: "dec", label: "DEC" }]} />
      <Segmented value={p.signed ? "s" : "u"} onChange={(v) => p.set({ signed: v === "s" })} options={[{ value: "u", label: t("binary.unsigned") }, { value: "s", label: t("binary.signed") }]} />
      <Segmented value={p.order} onChange={(v) => p.set({ order: v })} options={[
        { value: "Big", label: "HiLo (BE)", title: t("editor.hiLoTip") }, { value: "Little", label: "LoHi (LE)", title: t("editor.loHiTip") },
      ]} />
      {ascii && (
        <label className="flex items-center gap-1 text-xs text-fg-muted">
          <input type="checkbox" checked={p.showAscii} onChange={(e) => p.set({ showAscii: e.target.checked })} />ASCII
        </label>
      )}
      <label className="flex items-center gap-1 text-xs text-fg-muted" title={t("editor.padding.settingTip")}>
        <input type="checkbox" checked={p.hidePadding} onChange={(e) => p.set({ hidePadding: e.target.checked })} />{t("editor.padding.setting")}
      </label>
    </div>
  );
}
