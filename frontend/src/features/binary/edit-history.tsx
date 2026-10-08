"use client";
import { Redo2, RotateCcw, Undo2 } from "lucide-react";
import type { EditState } from "@/types/editing";
import { Button, KV, SectionTitle } from "@/components/ui";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

/**
 * Edit history of the working buffer (newest first), undo/redo and revert. Reverting writes the original bytes back
 * through the patch model; it is itself an undoable history entry.
 */
export function EditHistory({ state, selection, mapName, busy, onJump, onUndo, onRedo, onRevertSelection, onRevertRange, onRevertAll }: {
  state: EditState;
  selection: { start: number; end: number } | null;
  mapName: (mapId: string) => string | null;
  busy: boolean;
  onJump: (offset: number, length?: number) => void;
  onUndo: () => void;
  onRedo: () => void;
  onRevertSelection: () => void;
  onRevertRange: (start: number, length: number) => void;
  onRevertAll: () => void;
}) {
  const t = useT();
  const selChanged = !!selection && state.changedRanges.some((r) => r.start < selection.end && r.start + r.length > selection.start);
  return (
    <div className="space-y-3">
      <div className="rounded-md border border-border bg-bg p-2">
        <KV k={t("editor.changedBytes")}><span className={cn("num", state.changedBytes > 0 && "font-semibold text-warn")}>{state.changedBytes.toLocaleString()}</span></KV>
        <KV k={t("editor.originalSha")}><span className="num text-fg-muted" title={state.originalSha256}>{state.originalSha256.slice(0, 16)}…</span></KV>
        <KV k={t("editor.workingSha")}>
          <span className={cn("num", state.workingSha256 === state.originalSha256 ? "text-fg-muted" : "text-warn")} title={state.workingSha256}>{state.workingSha256.slice(0, 16)}…</span>
        </KV>
        <div className="mt-1 text-[10px] text-fg-subtle">{state.workingSha256 === state.originalSha256 ? t("editor.shaEqual") : t("editor.shaDiffer")}</div>
      </div>

      <div className="flex flex-wrap gap-1">
        <Button size="xs" disabled={!state.canUndo || busy} onClick={onUndo} title="Ctrl+Z"><Undo2 className="size-3" />{t("editor.undo")}</Button>
        <Button size="xs" disabled={!state.canRedo || busy} onClick={onRedo} title="Ctrl+Shift+Z"><Redo2 className="size-3" />{t("editor.redo")}{state.redoCount > 0 && <span className="num text-fg-subtle">({state.redoCount})</span>}</Button>
        <Button size="xs" disabled={!selChanged || busy} onClick={onRevertSelection} title={t("editor.revertSelectionTip")}><RotateCcw className="size-3" />{t("editor.revertSelection")}</Button>
        <Button size="xs" variant="danger" disabled={state.changedBytes === 0 || busy} onClick={onRevertAll}><RotateCcw className="size-3" />{t("editor.revertAll")}</Button>
      </div>

      <div>
        <SectionTitle className="mb-1">{t("editor.changedRegions", { n: state.regions.length })}</SectionTitle>
        <div className="max-h-48 space-y-0.5 overflow-y-auto">
          {state.regions.map((g) => (
            <div key={g.start} className="group flex items-center gap-1 rounded px-1 py-0.5 text-[11px] hover:bg-panel-2">
              <button className="num text-warn hover:underline" onClick={() => onJump(g.start, g.length)}>{hex(g.start)}</button>
              <span className="num text-fg-subtle">{t("editor.bytesN", { n: g.length })}</span>
              <span className="flex-1 truncate text-fg-muted">{g.mapName ?? (g.mapId ? mapName(g.mapId) ?? g.mapId : t("editor.outsideMaps"))}</span>
              <button className="invisible text-fg-subtle hover:text-danger group-hover:visible" title={t("editor.revertRegion")} disabled={busy} onClick={() => onRevertRange(g.start, g.length)}><RotateCcw className="size-3" /></button>
            </div>
          ))}
          {!state.regions.length && <div className="text-[11px] text-fg-subtle">{t("editor.noChanges")}</div>}
        </div>
      </div>

      <div>
        <SectionTitle className="mb-1">{t("editor.history", { n: state.history.length })}</SectionTitle>
        <div className="space-y-1">
          {state.history.map((h, i) => (
            <button key={h.id} onClick={() => onJump(h.firstAddress)} className={cn("block w-full rounded border border-border px-2 py-1 text-left text-[11px] hover:bg-panel-2", i === 0 && "border-calc/40")}>
              <div className="flex items-center gap-2">
                <span className="num text-fg-subtle">{new Date(h.at).toLocaleTimeString(t.lang === "ru" ? "ru-RU" : "en-US")}</span>
                <span className="rounded bg-panel-2 px-1 text-[10px] uppercase text-fg-muted">{t.tx(`editor.source.${h.source}`, h.source)}</span>
                <span className="num ml-auto text-calc">{hex(h.firstAddress)}</span>
              </div>
              {h.mapId && <div className="truncate text-fg">{mapName(h.mapId) ?? h.mapId}</div>}
              <div className="flex gap-2 text-fg-muted">
                <span className="flex-1 truncate" title={h.description}>{h.description}</span>
                <span className="num shrink-0">{t("editor.bytesN", { n: h.changedBytes })}</span>
              </div>
            </button>
          ))}
          {!state.history.length && <div className="text-[11px] text-fg-subtle">{t("editor.noHistory")}</div>}
        </div>
      </div>
    </div>
  );
}
