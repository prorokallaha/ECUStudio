"use client";
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { CornerDownRight, FileWarning, Pencil, Redo2, Save, Undo2 } from "lucide-react";
import { toast } from "sonner";
import type { AnalysisReport, ProjectFile } from "@/types/domain";
import type { EditState } from "@/types/editing";
import { PageHeader, PageSkeleton } from "@/components/layout/page";
import { Badge, Button, EmptyState, Input, KV, SectionTitle, Segmented, Select, Tabs } from "@/components/ui";
import { EditGrid, type EditGridHandle } from "@/components/binary/edit-grid";
import { useWorkingBuffer } from "@/components/binary/use-working-buffer";
import {
  ROW, analysisRegions, decode, encode, findPaddingRuns, fmtFloat, regionsFor, rowMask, toHex, useRowLayout, wordBytes,
} from "@/components/binary/row-model";
import { DisplayToolbar } from "@/components/binary/display-toolbar";
import { useProject, useReport } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useEditorPrefs } from "@/stores/editor";
import { useEditActions, useEditState, useFileContent } from "./use-edits";
import { EditHistory } from "./edit-history";
import { SaveDialog } from "./save-dialog";
import { ApiError } from "@/services/api";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

const MAX_PENDING = 4096;

/** Picks the file to edit: ?file=, else the analysed modified file, else the first Modified/Stock file. */
function pickDefault(files: ProjectFile[], report: AnalysisReport | undefined): ProjectFile | undefined {
  return files.find((f) => f.sha256 === report?.modifiedSha256) ?? files.find((f) => f.role === "Modified") ?? files[0];
}

/** Safe binary editor: working buffer view, hex edits through the backend patch model, history, save-as-new. */
export function BinaryEditor() {
  const t = useT();
  const { projectId, params, setParam } = useWorkspace();
  const project = useProject(projectId);
  const report = useReport();
  const files = useMemo(() => project.data?.files ?? [], [project.data]);
  const file = files.find((f) => f.id === params.get("file")) ?? pickDefault(files, report.data);

  if (!projectId) return <EmptyState icon={<FileWarning className="size-8" />} title={t("layout.noBinary")}>{t("layout.noBinaryHint")}</EmptyState>;
  if (project.isLoading) return <PageSkeleton />;
  if (!file) return <EmptyState icon={<FileWarning className="size-8" />} title={t("layout.noBinary")}>{t("layout.noBinaryHint")}</EmptyState>;
  return <EditorBody key={file.id} projectId={projectId} file={file} files={files} report={report.data} onPickFile={(id) => setParam("file", id)} />;
}

function EditorBody({ projectId, file, files, report, onPickFile }: { projectId: string; file: ProjectFile; files: ProjectFile[]; report?: AnalysisReport; onPickFile: (id: string) => void }) {
  const t = useT();
  const prefs = useEditorPrefs();
  const content = useFileContent(projectId, file.id);
  const editState = useEditState(projectId, file.id);
  const actions = useEditActions(projectId, file.id);
  const state = editState.data;
  const original = content.data;
  const size = state?.size ?? original?.length ?? file.size;
  const buf = useWorkingBuffer(projectId, file.id, original, state);
  const grid = useRef<EditGridHandle>(null);

  const n = wordBytes(prefs.word);
  const [cursor, setCursor] = useState<number | null>(null);
  const [anchor, setAnchor] = useState<number | null>(null);
  const [pending, setPending] = useState<{ start: number; nibbles: string } | null>(null);
  const [scrollTo, setScrollTo] = useState<{ offset: number; seq: number } | null>(null);
  const [goto, setGoto] = useState("");
  const [tab, setTab] = useState<"inspect" | "history">("inspect");
  const [saveOpen, setSaveOpen] = useState(false);

  // Padding folding: computed from the stored original; rows touched by unsaved edits are never folded.
  const runs = useMemo(() => {
    if (!original || !prefs.hidePadding) return [];
    return findPaddingRuns(original, size, { skipRows: rowMask(size, state?.changedRanges ?? []) });
  }, [original, size, state?.changedRanges, prefs.hidePadding]);
  const { layout, toggle, reveal } = useRowLayout(size, runs, prefs.hidePadding);

  const regions = useMemo(() => (report && report.ecu.flashSize === size ? analysisRegions(report) : []), [report, size]);
  const mapName = useCallback((id: string) => report?.maps.find((m) => m.id === id)?.name ?? null, [report]);

  const align = useCallback((o: number) => o - (o % n), [n]);
  const selection = useMemo(() => {
    if (cursor === null) return null;
    const a = Math.min(anchor ?? cursor, cursor), z = Math.max(anchor ?? cursor, cursor);
    return { start: a, end: Math.min(size, z + n) };
  }, [cursor, anchor, size, n]);

  const jump = useCallback((offset: number, length = 1) => {
    const o = Math.max(0, Math.min(size - 1, offset));
    reveal(o);
    setPending(null);
    setAnchor(align(o));
    setCursor(align(Math.min(size - 1, o + Math.max(1, length) - 1)));
    setScrollTo({ offset: o, seq: Date.now() });
  }, [size, reveal, align]);

  // Keep cursor aligned when word size changes.
  useEffect(() => { setCursor((c) => (c === null ? c : align(c))); setAnchor((a) => (a === null ? a : align(a))); }, [align]);

  const ensure = buf.ensure;
  const onVisible = useCallback((from: number, to: number) => ensure(from, to), [ensure]);

  // ── pending (typed but not yet written) bytes ──
  const pendingLen = pending ? Math.ceil(pending.nibbles.length / 2) : 0;
  const pendingAt = useCallback((o: number): number | null => {
    if (!pending || o < pending.start || o >= pending.start + pendingLen) return null;
    const i = o - pending.start;
    const hi = parseInt(pending.nibbles[2 * i], 16);
    const lo = pending.nibbles[2 * i + 1];
    return lo !== undefined ? (hi << 4) | parseInt(lo, 16) : (hi << 4) | ((buf.working(o) ?? 0) & 0x0f);
  }, [pending, pendingLen, buf]);

  const commitPending = useCallback(() => {
    if (!pending || !pending.nibbles) { setPending(null); return; }
    const bytes = Array.from({ length: pendingLen }, (_, i) => pendingAt(pending.start + i) ?? 0);
    const start = pending.start;
    actions.write.mutate({ address: start, hex: toHex(bytes) }, {
      onSuccess: () => { setPending(null); const c = align(Math.min(size - 1, start + bytes.length)); setCursor(c); setAnchor(c); },
    });
  }, [pending, pendingLen, pendingAt, actions.write, align, size]);

  const undo = useCallback(() => { if (state?.canUndo && !actions.busy) { setPending(null); actions.undo.mutate(); } }, [state?.canUndo, actions]);
  const redo = useCallback(() => { if (state?.canRedo && !actions.busy) { setPending(null); actions.redo.mutate(); } }, [state?.canRedo, actions]);

  // Ctrl+Z / Ctrl+Shift+Z while focus is anywhere in the editor (except text fields, which keep native undo).
  // Stopping propagation keeps the workspace-wide undo (hardware/decisions) from also firing.
  const onEditorKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const mod = e.ctrlKey || e.metaKey;
    if (!mod || saveOpen) return;
    const k = e.key.toLowerCase();
    if (k !== "z" && k !== "y") return;
    const tg = e.target as HTMLElement;
    if (tg.tagName === "INPUT" || tg.tagName === "TEXTAREA") return;
    e.preventDefault(); e.stopPropagation();
    if (k === "y" || e.shiftKey) redo(); else undo();
  };

  const onGridKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (pending) {
      if (e.key === "Enter") { e.preventDefault(); commitPending(); return; }
      if (e.key === "Escape") { e.preventDefault(); setPending(null); return; }
      if (e.key === "Backspace") { e.preventDefault(); setPending((p) => (p && p.nibbles.length > 1 ? { ...p, nibbles: p.nibbles.slice(0, -1) } : null)); return; }
    }
    if (/^[0-9a-fA-F]$/.test(e.key)) {
      e.preventDefault();
      if (actions.write.isPending) return;
      const start = pending?.start ?? selection?.start ?? null;
      if (start === null) return;
      const cur = pending?.nibbles ?? "";
      if (cur.length >= 2 * Math.min(MAX_PENDING, size - start)) return;
      setPending({ start, nibbles: cur + e.key.toUpperCase() });
      return;
    }
    if (pending) return;
    if (e.key === "Escape") { setAnchor(cursor); return; }
    const step = ({ ArrowRight: n, ArrowLeft: -n, ArrowDown: ROW, ArrowUp: -ROW, PageDown: ROW * 32, PageUp: -ROW * 32 } as Record<string, number>)[e.key];
    if (step && cursor !== null) {
      e.preventDefault();
      let next = align(Math.max(0, Math.min(size - 1, cursor + step)));
      // Arrow keys step over a folded padding run instead of expanding it.
      const run = layout.foldedRunAt(next);
      if (run) next = align(step > 0 ? Math.min(size - 1, run.end + (cursor % ROW)) : Math.max(0, run.start - ROW + (cursor % ROW)));
      reveal(next);
      setCursor(next);
      if (!e.shiftKey) setAnchor(next);
      grid.current?.scrollToOffset(next);
    }
  };

  const onCellDown = (off: number, extend: boolean) => {
    if (pending) setPending(null);
    setCursor(off);
    if (!extend || anchor === null) setAnchor(off);
  };

  const revertSelection = () => {
    if (!selection) return;
    actions.revert.mutate({ ranges: [{ start: selection.start, length: selection.end - selection.start, end: selection.end }] });
  };
  const revertAll = () => { if (window.confirm(t("editor.revertAllConfirm"))) actions.revert.mutate({ all: true }); };

  const loadError = content.error ?? editState.error;
  const changed = state?.changedBytes ?? 0;

  return (
    <div className="flex h-full min-h-0 flex-col" onKeyDown={onEditorKey}>
      <PageHeader
        title={<span className="flex items-center gap-1.5"><Pencil className="size-3.5" />{t("editor.title")}</span>}
        subtitle={t("editor.subtitle", { name: file.name, size: size.toLocaleString(), changed })}
        actions={<>
          <form className="flex items-center gap-1" onSubmit={(e) => { e.preventDefault(); const v = parseInt(goto.replace(/^0x/i, ""), 16); if (Number.isFinite(v)) { jump(v); grid.current?.focus(); } }}>
            <Input value={goto} onChange={(e) => setGoto(e.target.value)} placeholder={t("binary.gotoPlaceholder")} className="num w-28" />
            <Button size="icon" type="submit" title={t("binary.gotoTitle")}><CornerDownRight className="size-3.5" /></Button>
          </form>
          <Button size="icon" variant="ghost" disabled={!state?.canUndo || actions.busy} onClick={undo} title={`${t("editor.undo")} (Ctrl+Z)`}><Undo2 className="size-3.5" /></Button>
          <Button size="icon" variant="ghost" disabled={!state?.canRedo || actions.busy} onClick={redo} title={`${t("editor.redo")} (Ctrl+Shift+Z)`}><Redo2 className="size-3.5" /></Button>
          <Button variant="primary" disabled={!changed || actions.busy || !!pending} onClick={() => setSaveOpen(true)} title={t("editor.saveTip")}><Save className="size-3.5" />{t("editor.saveAsNew")}</Button>
        </>}
      />
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-1.5">
        <Select value={file.id} onChange={(e) => onPickFile(e.target.value)} className="max-w-72" title={t("editor.file")}>
          {files.map((f) => <option key={f.id} value={f.id}>{t.tx(`fileRole.${f.role}`, f.role)} · {f.label || f.name} ({f.name})</option>)}
        </Select>
        <DisplayToolbar />
        <span className="ml-auto text-[11px] text-fg-subtle">{t("editor.originalSafe")}</span>
      </div>
      {pending && (
        <div className="flex items-center gap-3 border-b border-attn/40 bg-attn/10 px-3 py-1 text-xs">
          <span className="font-medium">{t("editor.pending", { addr: hex(pending.start), n: pendingLen })}</span>
          <span className="num truncate text-fg-muted">{pending.nibbles.replace(/(..)/g, "$1 ").trim()}</span>
          <span className="ml-auto text-fg-subtle">{t("editor.pendingHint")}</span>
          <Button size="xs" variant="primary" onClick={commitPending} disabled={actions.write.isPending}>{t("editor.write")}</Button>
          <Button size="xs" variant="ghost" onClick={() => setPending(null)}>{t("editor.cancel")}</Button>
        </div>
      )}
      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1">
          {loadError ? (
            <EmptyState icon={<FileWarning className="size-8 text-danger" />} title={t("layout.loadFailed")}>{loadError instanceof ApiError ? `${loadError.code}: ${loadError.message}` : String(loadError)}</EmptyState>
          ) : !original || !state ? <PageSkeleton /> : (
            <EditGrid ref={grid} size={size} layout={layout} onToggleFold={toggle} working={buf.working} original={buf.original} isChanged={buf.isChanged} pendingAt={pendingAt}
              word={prefs.word} base={prefs.base} signed={prefs.signed} order={prefs.order} showAscii={prefs.showAscii}
              selection={selection} cursor={cursor} scrollTo={scrollTo} onVisible={onVisible} onCellDown={onCellDown} onKeyDown={onGridKey} />
          )}
        </div>
        <aside className="flex w-80 shrink-0 flex-col border-l border-border bg-bg-elev">
          <Tabs value={tab} onChange={setTab} items={[{ value: "inspect", label: t("editor.tabInspect") }, { value: "history", label: t("editor.tabHistory", { n: state?.history.length ?? 0 }) }]} />
          <div className="min-h-0 flex-1 overflow-y-auto p-3">
            {state && tab === "history" && (
              <EditHistory state={state} selection={selection} mapName={mapName} busy={actions.busy} onJump={jump} onUndo={undo} onRedo={redo}
                onRevertSelection={revertSelection} onRevertRange={(start, length) => actions.revert.mutate({ ranges: [{ start, length, end: start + length }] })} onRevertAll={revertAll} />
            )}
            {state && tab === "inspect" && (
              <Inspector state={state} selection={selection} cursor={cursor} working={buf.working} original={buf.original} isChanged={buf.isChanged}
                regions={regions} busy={actions.busy}
                onWrite={(address, bytes, description) => actions.write.mutate({ address, hex: toHex(bytes), description: description || null })} />
            )}
          </div>
        </aside>
      </div>
      <SaveDialog open={saveOpen} onClose={() => setSaveOpen(false)} projectId={projectId} file={file} onOpenFile={onPickFile} />
    </div>
  );
}

function Inspector({ state, selection, cursor, working, original, isChanged, regions, busy, onWrite }: {
  state: EditState;
  selection: { start: number; end: number } | null;
  cursor: number | null;
  working: (o: number) => number | null;
  original: (o: number) => number | null;
  isChanged: (o: number) => boolean;
  regions: ReturnType<typeof analysisRegions>;
  busy: boolean;
  onWrite: (address: number, bytes: Uint8Array | number[], description?: string) => void;
}) {
  const t = useT();
  const prefs = useEditorPrefs();
  const at = selection?.start ?? cursor;
  const selLen = selection ? selection.end - selection.start : 0;
  const [hexIn, setHexIn] = useState("");
  const [valIn, setValIn] = useState("");
  const [desc, setDesc] = useState("");

  // Prefill the hex field with the selected working bytes whenever they change (selection, edit, page load).
  let selHex = "";
  if (selection) {
    const bytes: number[] = [];
    for (let i = 0; i < Math.min(256, selection.end - selection.start); i++) { const b = working(selection.start + i); if (b === null) { bytes.length = 0; break; } bytes.push(b); }
    selHex = toHex(bytes, " ");
  }
  useEffect(() => { setHexIn(selHex); }, [selHex]);

  if (at === null) return <div className="text-xs text-fg-muted">{t("editor.inspectHint")}</div>;

  const w4 = [0, 1, 2, 3].map((k) => working(at + k));
  const o4 = [0, 1, 2, 3].map((k) => original(at + k));
  const rows: [string, number | null, number | null][] = ([["8", false], ["8", true], ["16", false], ["16", true], ["32", false], ["32", true], ["f32", false]] as const)
    .map(([w, s]) => [`${w === "f32" ? "f32" : (s ? "i" : "u") + w}`, decode(w4, w, s, prefs.order), decode(o4, w, s, prefs.order)]);
  const fmt = (v: number | null, f32: boolean) => (v === null ? "—" : f32 ? fmtFloat(v) : prefs.base === "hex" ? (v < 0 ? "-0x" + (-v).toString(16).toUpperCase() : "0x" + v.toString(16).toUpperCase()) : String(v));
  const regs = regionsFor(regions, at, at + 1);
  const changedHere = isChanged(at);
  const typeLabel = `${prefs.word === "f32" ? "float32" : (prefs.signed ? "int" : "uint") + prefs.word} ${prefs.order === "Big" ? "HiLo (BE)" : "LoHi (LE)"}`;

  const submitHex = () => {
    const clean = hexIn.replace(/0x/gi, "").replace(/[\s,]/g, "");
    if (!clean || clean.length % 2 || /[^0-9a-f]/i.test(clean)) { toast.error(t("editor.badHex")); return; }
    const bytes = clean.match(/../g)!.map((x) => parseInt(x, 16));
    if (at + bytes.length > state.size) { toast.error(t("editor.pastEnd")); return; }
    onWrite(at, bytes, desc.trim());
  };
  const submitVal = () => {
    const bytes = encode(valIn, prefs.word, prefs.signed, prefs.order);
    if (!bytes) { toast.error(t("editor.badValue", { type: typeLabel })); return; }
    if (at + bytes.length > state.size) { toast.error(t("editor.pastEnd")); return; }
    onWrite(at, bytes, desc.trim() || t("editor.valueWriteDesc", { value: valIn.trim(), type: typeLabel }));
  };

  return (
    <div className="space-y-3 text-xs">
      <div className="flex items-center justify-between">
        <span className="num text-sm font-semibold">{hex(at)}</span>
        {selLen > 1 && <span className="num text-fg-muted">{t("editor.selection", { n: selLen, end: hex(at + selLen - 1) })}</span>}
      </div>
      {changedHere && <Badge tone="warn">{t("editor.changedHere")}</Badge>}
      {regs.map((g, i) => (
        <div key={i} className="flex items-center gap-1.5">
          <Badge tone={g.kind === "candidate" ? "ai" : g.kind === "section" ? "unknown" : "ok"}>{t.tx(`binary.region.${g.kind}`, g.kind)}</Badge>
          <span className="truncate">{g.kind === "section" ? t.tx(`sectionKind.${g.label}`, g.label) : g.label}</span>
        </div>
      ))}
      <div>
        <SectionTitle className="mb-1">{t("editor.values", { order: prefs.order === "Big" ? "HiLo (BE)" : "LoHi (LE)" })}</SectionTitle>
        <table className="w-full">
          <thead className="text-[10px] text-fg-subtle"><tr><th className="text-left font-medium">{t("binary.colType")}</th><th className="text-right font-medium">{t("editor.colWorking")}</th><th className="text-right font-medium">{t("editor.colOriginal")}</th></tr></thead>
          <tbody>{rows.map(([ty, wv, ov]) => (
            <tr key={ty} className={cn(wv !== ov && "text-warn")}><td className="text-fg-subtle">{ty}</td><td className="num text-right">{fmt(wv, ty === "f32")}</td><td className="num text-right text-fg-muted">{fmt(ov, ty === "f32")}</td></tr>
          ))}</tbody>
        </table>
        <div className="mt-1 text-[10px] text-fg-subtle">{t("editor.displayOnly")}</div>
      </div>
      <div className="space-y-1.5 rounded-md border border-border p-2">
        <SectionTitle>{t("editor.writeTitle", { addr: hex(at) })}</SectionTitle>
        <form className="flex gap-1" onSubmit={(e) => { e.preventDefault(); submitHex(); }}>
          <Input value={hexIn} onChange={(e) => setHexIn(e.target.value)} placeholder="AA BB CC" className="num" />
          <Button type="submit" disabled={busy}>{t("editor.writeHex")}</Button>
        </form>
        <form className="flex gap-1" onSubmit={(e) => { e.preventDefault(); submitVal(); }}>
          <Input value={valIn} onChange={(e) => setValIn(e.target.value)} placeholder={typeLabel} className="num" />
          <Button type="submit" disabled={busy}>{t("editor.writeValue")}</Button>
        </form>
        <Input value={desc} onChange={(e) => setDesc(e.target.value)} placeholder={t("editor.descPlaceholder")} />
        <div className="text-[10px] text-fg-subtle">{t("editor.writeHint")}</div>
      </div>
      <KV k={t("editor.changedBytes")}><span className="num">{state.changedBytes.toLocaleString()}</span></KV>
      <div className="text-[10px] text-fg-subtle">{t("editor.typeHint")}</div>
    </div>
  );
}
