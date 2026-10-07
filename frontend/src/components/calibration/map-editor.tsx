"use client";
import { memo, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import Link from "next/link";
import { toast } from "sonner";
import { Binary, Box, GitCompare, Grid3x3, LineChart, Lock, PencilLine, SplitSquareHorizontal, Table2 } from "lucide-react";
import type { AnalysisReport, MapData } from "@/types/domain";
import type { EditState, MapEditPreview, MapEditorView, MapOperation, MapOperationKind, WorkingMapValues } from "@/types/maps-edit";
import { Badge, ConfidenceBadge, KV, SectionTitle, Segmented, SeverityBadge, SourceBadge, Tabs } from "@/components/ui";
import { MapTable, emptySelection, selectedIndices, selectionCount, type CellSelection, type TableKind } from "./map-table";
import { MapEditConfirm, MapEditToolbar } from "./map-edit-toolbar";
import { MapSurface3D } from "./map-surface-3d";
import { Map2D, MapHeatmap } from "./map-charts";
import type { CellRef } from "./map-grid";
import { isWeakMap, mapLabel, roleCounts } from "./map-label";
import { editedMapIds, typeSize, useMapEditActions, useWorkingMap, type EditTarget } from "@/features/maps/use-map-edits";
import { useSelection } from "@/stores/selection";
import { useWorkspace } from "@/hooks/use-workspace";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

interface Pending { op: MapOperation; title: string; preview: MapEditPreview }

function decimalsOf(data: MapData): number {
  const s = data.summary;
  if (s.dataType === "Float32") return 3;
  return Math.max(0, Math.min(4, Math.ceil(-Math.log10(Math.abs(s.factor) || 1) - 1e-9)));
}

/** "1\t2\n3\t4" → rows of numbers (tab, semicolon or whitespace separated; decimal comma accepted when not a separator). */
function parseTsv(text: string): number[][] | null {
  const lines = text.replace(/\r/g, "").split("\n").filter((l) => l.trim() !== "");
  if (!lines.length) return null;
  const sep = text.includes("\t") ? /\t/ : text.includes(";") ? /;/ : /\s+/;
  const out: number[][] = [];
  for (const line of lines) {
    const row: number[] = [];
    for (const raw of line.trim().split(sep)) {
      const v = Number(raw.trim().replace(",", ".").replace("−", "-"));
      if (raw.trim() === "" || !Number.isFinite(v)) return null;
      row.push(v);
    }
    out.push(row);
  }
  return out;
}

/**
 * WinOLS-like editor of one map: table with selection and operations, 2D, 3D, heatmap, raw hex and stock-vs-mod.
 * Values come from the working buffer of the edited file (patches applied, not re-analysed). Every operation is first
 * previewed and only applied as one undoable patch after explicit confirmation.
 */
export function MapEditor({ r, data, target, editState, initialView = "table", onViewChange }: {
  r: AnalysisReport; data: MapData; target: EditTarget | null; editState?: EditState | null;
  initialView?: MapEditorView; onViewChange?: (v: MapEditorView) => void;
}) {
  const t = useT();
  const { href } = useWorkspace();
  const select = useSelection((s) => s.select);
  const s = data.summary;
  const cols = data.xAxis.length || 1;
  const rows = data.yAxis.length || 1;
  const n = rows * cols;
  const decimals = decimalsOf(data);
  const label = useMemo(() => mapLabel(t, s, roleCounts(r.maps)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [s, r.maps, t.lang]);

  const [rawView, setViewState] = useState<MapEditorView>(initialView);
  const view: MapEditorView = (rawView === "3d" && (rows < 2 || cols < 2)) || (rawView === "compare" && !data.stockValues) ? "table" : rawView;
  const setView = (v: MapEditorView) => { setViewState(v); onViewChange?.(v); };
  useEffect(() => { setViewState(initialView); }, [initialView]);
  const [sel, setSel] = useState<CellSelection>(() => emptySelection(n));
  const [operand, setOperand] = useState("");
  const [pending, setPending] = useState<Pending | null>(null);
  const [busy, setBusy] = useState(false);
  const [heat, setHeat] = useState(true);
  const [onlyChanged, setOnlyChanged] = useState(false);
  const [deltaKind, setDeltaKind] = useState<"delta" | "deltaPct">("deltaPct");
  const operandRef = useRef<HTMLInputElement>(null);

  useEffect(() => { setSel(emptySelection(n)); setPending(null); }, [data.id, n]);

  const working = useWorkingMap(target, data);
  const w: WorkingMapValues | null = working.data ?? null;
  const values = w?.values ?? data.values;
  const editedCells = w?.editedCells ?? 0;
  const workingBroken = !!target && working.isSuccess && !w;
  const canEdit = !!target && !!w && !working.isFetching;
  const actions = useMapEditActions(target, s.id);

  const sameAxes = !!data.stockValues && JSON.stringify(data.stockXAxis) === JSON.stringify(data.xAxis) && JSON.stringify(data.stockYAxis) === JSON.stringify(data.yAxis);
  const stock = sameAxes ? data.stockValues : null;

  const pendingMap = useMemo(() => pending ? new Map(pending.preview.changes.map((c) => [c.row * cols + c.col, c.stored])) : null, [pending, cols]);
  const shown = useMemo(() => {
    if (!pendingMap?.size) return values;
    const v = values.slice();
    pendingMap.forEach((x, i) => { v[i] = x; });
    return v;
  }, [values, pendingMap]);

  // Selection → global selection store (AI context), debounced so drag-selecting does not flood subscribers.
  useEffect(() => {
    const id = setTimeout(() => {
      const idx = selectedIndices(sel);
      select({
        kind: "map", mapId: s.id, mapName: label, unit: s.unit,
        cells: idx.slice(0, 64).map((i) => ({ row: Math.floor(i / cols), col: i % cols, x: data.xAxis[i % cols], y: data.yAxis[Math.floor(i / cols)], mod: values[i], stock: stock?.[i] ?? null })),
      });
    }, 200);
    return () => clearTimeout(id);
  }, [sel, values, stock, s.id, s.unit, label, cols, data.xAxis, data.yAxis, select]);

  const count = useMemo(() => selectionCount(sel), [sel]);

  const opTitle = useCallback((kind: MapOperationKind, operand: number) => t.tx(`mapEditor.opTitle.${kind}`, kind, { v: operand }), [t]);

  const requestOp = useCallback(async (kind: MapOperationKind, operandValue: number, cellsIdx?: number[], vals?: number[]) => {
    if (!canEdit) return;
    const idx = cellsIdx ?? selectedIndices(sel);
    if (!idx.length) { toast(t("mapEditor.selectFirst")); return; }
    const op: MapOperation = { kind, operand: operandValue, cells: idx.map((i) => ({ row: Math.floor(i / cols), col: i % cols })), values: vals ?? null };
    setBusy(true);
    const preview = await actions.preview(op);
    setBusy(false);
    if (preview) setPending({ op, title: opTitle(kind, operandValue), preview });
  }, [canEdit, sel, cols, actions, opTitle, t]);

  const apply = async () => {
    if (!pending) return;
    setBusy(true);
    const st = await actions.apply(pending.op);
    setBusy(false);
    if (st) {
      toast.success(t("mapEditor.applied", { n: pending.preview.changedBytes }));
      setPending(null);
    }
  };

  const copyText = useCallback((): string | null => {
    const idx = selectedIndices(sel);
    if (!idx.length) return null;
    let r0 = Infinity, r1 = -1, c0 = Infinity, c1 = -1;
    for (const i of idx) { const rr = Math.floor(i / cols), cc = i % cols; r0 = Math.min(r0, rr); r1 = Math.max(r1, rr); c0 = Math.min(c0, cc); c1 = Math.max(c1, cc); }
    const lines: string[] = [];
    for (let rr = r0; rr <= r1; rr++) {
      const cells: string[] = [];
      for (let cc = c0; cc <= c1; cc++) { const i = rr * cols + cc; cells.push(sel.mask[i] ? values[i].toFixed(decimals) : ""); }
      lines.push(cells.join("\t"));
    }
    return lines.join("\n");
  }, [sel, cols, values, decimals]);

  const copy = async () => {
    const text = copyText();
    if (text === null) return;
    try { await navigator.clipboard.writeText(text); toast(t("mapEditor.copied", { n: count })); } catch { toast.error(t("mapEditor.clipboardDenied")); }
  };

  const pasteText = useCallback((text: string) => {
    const block = parseTsv(text);
    if (!block) { toast.error(t("mapEditor.pasteInvalid")); return; }
    const idx = selectedIndices(sel);
    const cellsIdx: number[] = [], vals: number[] = [];
    if (block.length === 1 && block[0].length === 1 && idx.length > 1) {
      for (const i of idx) { cellsIdx.push(i); vals.push(block[0][0]); }
    } else {
      const start = idx.length ? idx[0] : Math.max(0, sel.focus);
      let r0 = Math.floor(start / cols), c0 = start % cols;
      for (const i of idx) { r0 = Math.min(r0, Math.floor(i / cols)); c0 = Math.min(c0, i % cols); }
      block.forEach((row, dr) => row.forEach((v, dc) => {
        const rr = r0 + dr, cc = c0 + dc;
        if (rr < rows && cc < cols) { cellsIdx.push(rr * cols + cc); vals.push(v); }
      }));
      if (block.length > rows - r0 || block.some((row) => row.length > cols - c0)) toast(t("mapEditor.pasteClipped"));
    }
    if (!cellsIdx.length) return;
    const mask = new Uint8Array(n);
    for (const i of cellsIdx) mask[i] = 1;
    setSel({ mask, anchor: cellsIdx[0], focus: cellsIdx[cellsIdx.length - 1] });
    requestOp("Values", 0, cellsIdx, vals);
  }, [sel, cols, rows, n, requestOp, t]);

  const paste = async () => {
    try { pasteText(await navigator.clipboard.readText()); } catch { toast.error(t("mapEditor.clipboardDenied")); }
  };

  const revert = async () => {
    setBusy(true);
    const st = await actions.revert();
    setBusy(false);
    setPending(null);
    if (st) toast(t("mapEditor.reverted"));
  };
  const undo = async () => { setPending(null); setBusy(true); await actions.undo(); setBusy(false); };
  const redo = async () => { setPending(null); setBusy(true); await actions.redo(); setBusy(false); };

  const onTypeStart = useCallback((key: string) => {
    setOperand(key);
    requestAnimationFrame(() => operandRef.current?.focus());
  }, []);

  const pickCell = useCallback((i: number, additive: boolean) => {
    setSel((cur) => {
      const mask = additive ? cur.mask.slice() : new Uint8Array(n);
      mask[i] = additive && cur.mask[i] ? 0 : 1;
      return { mask, anchor: i, focus: i };
    });
  }, [n]);
  const pickRef = useCallback((c: CellRef) => pickCell(c.row * cols + c.col, false), [pickCell, cols]);

  const chartData = useMemo(() => ({ xAxis: data.xAxis, yAxis: data.yAxis, values: shown, stock, unit: s.unit, xName: s.xAxis.split(" ")[0], yName: s.yAxis.split(" ")[0] }), [data.xAxis, data.yAxis, shown, stock, s]);
  const selectedRefs = useMemo(() => selectedIndices(sel).slice(0, 1).map((i) => ({ row: Math.floor(i / cols), col: i % cols })), [sel, cols]);

  const mapEdited = editedMapIds(editState).has(s.id) || editedCells > 0;
  const history = (editState?.history ?? []).filter((h) => h.mapId === s.id);
  const firstSel = selectedIndices(sel)[0];
  const byteLen = n * typeSize(s.dataType);
  const binOffset = firstSel !== undefined && w ? w.addresses[firstSel] : s.address;
  const explanation = r.explanations?.find((e) => e.mapId === s.id);
  const stats = useMemo(() => {
    const idx = selectedIndices(sel);
    if (idx.length < 2) return null;
    let min = Infinity, max = -Infinity, sum = 0;
    for (const i of idx) { const v = values[i]; min = Math.min(min, v); max = Math.max(max, v); sum += v; }
    return { min, max, mean: sum / idx.length };
  }, [sel, values]);

  const tableProps = { xAxis: data.xAxis, yAxis: data.yAxis, decimals, selection: sel, onSelection: setSel };

  return (
    <div className="flex h-full min-h-0">
      <div className="flex min-w-0 flex-1 flex-col">
        <Tabs
          value={view}
          onChange={setView}
          items={[
            { value: "table", label: t("maps.tabTable"), icon: <Table2 className="size-3.5" /> },
            { value: "2d", label: "2D", icon: <LineChart className="size-3.5" /> },
            { value: "3d", label: "3D", icon: <Box className="size-3.5" />, hidden: rows < 2 || cols < 2 },
            { value: "heatmap", label: t("maps.tabHeatmap"), icon: <Grid3x3 className="size-3.5" /> },
            { value: "hex", label: t("mapEditor.tabRaw"), icon: <Binary className="size-3.5" /> },
            { value: "compare", label: t("mapEditor.tabCompare"), icon: <SplitSquareHorizontal className="size-3.5" />, hidden: !data.stockValues },
          ]}
          right={view === "compare" ? (
            <>
              <Segmented size="xs" value={deltaKind} onChange={setDeltaKind} options={[{ value: "deltaPct", label: "Δ%" }, { value: "delta", label: t("maps.deltaAbs") }]} />
              <label className="flex items-center gap-1 whitespace-nowrap text-[11px] text-fg-muted"><input type="checkbox" checked={onlyChanged} onChange={(e) => setOnlyChanged(e.target.checked)} />{t("maps.onlyChanged")}</label>
            </>
          ) : view === "table" ? (
            <label className="flex items-center gap-1 whitespace-nowrap text-[11px] text-fg-muted"><input type="checkbox" checked={heat} onChange={(e) => setHeat(e.target.checked)} />{t("mapEditor.heat")}</label>
          ) : null}
        />
        <MapEditToolbar
          ref={operandRef} operand={operand} setOperand={setOperand}
          count={count} disabled={!canEdit} busy={busy}
          canUndo={!!editState?.canUndo} canRedo={!!editState?.canRedo} canRevert={mapEdited}
          onOp={(k, v) => requestOp(k, v)} onCopy={copy} onPaste={paste} onRevert={revert} onUndo={undo} onRedo={redo}
        />
        {!target && <Notice tone="unknown">{t("mapEditor.noTarget")}</Notice>}
        {workingBroken && <Notice tone="warn">{t("mapEditor.workingUnreadable")}</Notice>}
        {pending && (
          <MapEditConfirm preview={pending.preview} title={pending.title} xAxis={data.xAxis} yAxis={data.yAxis} decimals={decimals} unit={s.unit} busy={busy}
            onApply={apply} onCancel={() => setPending(null)} />
        )}
        <div className="min-h-0 flex-1 overflow-auto p-3">
          {view === "table" && (
            <MapTable {...tableProps} values={values} base={data.values} stock={stock} heat={heat} pending={pendingMap}
              onCopy={copyText} onPaste={canEdit ? pasteText : undefined} onTypeStart={canEdit ? onTypeStart : undefined}
              onUndo={canEdit ? undo : undefined} onRedo={canEdit ? redo : undefined} onEscape={() => setPending(null)} />
          )}
          {view === "2d" && <div className="h-full min-h-80"><Map2D d={chartData} selected={selectedRefs} onPick={pickRef} /></div>}
          {view === "3d" && (
            <MapSurface3D xAxis={data.xAxis} yAxis={data.yAxis} values={shown} stock={stock} unit={s.unit} xName={chartData.xName} yName={chartData.yName}
              decimals={decimals} mask={sel.mask} onPick={pickCell} />
          )}
          {view === "heatmap" && (
            <div className="flex h-full min-h-80 flex-col gap-2">
              <div className="min-h-0 flex-1"><MapHeatmap d={chartData} onPick={pickRef} /></div>
              {stock && <div className="min-h-0 flex-1"><MapHeatmap d={chartData} delta onPick={pickRef} /></div>}
            </div>
          )}
          {view === "hex" && <RawHex data={data} w={w} values={values} mask={sel.mask} onPick={pickCell} binaryHref={(o) => href("binary", { offset: o })} />}
          {view === "compare" && data.stockValues && (
            sameAxes ? (
              <div className="@container"><div className="grid grid-cols-1 gap-3 @5xl:grid-cols-3">
                {(["stock", "values", deltaKind] as TableKind[]).map((k) => (
                  <div key={k} className="min-w-0 rounded-md border border-border">
                    <div className="border-b border-border px-2 py-1 text-[10px] font-semibold uppercase tracking-wider text-fg-subtle">
                      {k === "stock" ? t("common.stock") : k === "values" ? t("mapEditor.modCurrent") : k === "delta" ? t("mapEditor.deltaAbs") : t("mapEditor.deltaPct")}
                    </div>
                    <MapTable {...tableProps} values={values} base={data.values} stock={stock} kind={k} onlyChanged={onlyChanged} dense />
                  </div>
                ))}
              </div></div>
            ) : (
              <div className="space-y-2">
                <Badge tone="warn">{t("maps.axesChanged")}</Badge>
                <div className="text-xs text-fg-muted">{t("maps.axesChangedHint")}</div>
                <MapTable xAxis={data.stockXAxis ?? []} yAxis={data.stockYAxis ?? []} values={data.stockValues} decimals={decimals} selection={emptySelection(data.stockValues.length)} onSelection={() => {}} dense />
                <MapTable {...tableProps} values={values} base={data.values} dense />
              </div>
            )
          )}
        </div>
      </div>

      <aside className="w-72 shrink-0 space-y-3 overflow-y-auto border-l border-border bg-bg-elev p-3 text-xs">
        <div>
          <div className={cn("text-sm font-semibold", isWeakMap(s) && "text-fg-muted")}>{label}</div>
          {label !== s.name && !isWeakMap(s) && <div className="text-[11px] text-fg-subtle">{s.name}</div>}
          <div className="mt-1 flex flex-wrap gap-1">
            <SourceBadge source={s.source} /><ConfidenceBadge score={s.confidence} />
            {s.modified ? <Badge tone="calc">{t("maps.badgeModified")}</Badge> : <Badge tone="ok">{t("maps.badgeStock")}</Badge>}
            {mapEdited && <Badge tone="attn" title={t("mapEditor.editedHint")}><PencilLine className="size-3" />{t("mapEditor.editedNotAnalysed")}</Badge>}
          </div>
          {mapEdited && <div className="mt-1 text-[11px] text-attn">{t("mapEditor.editedCells", { n: editedCells })}</div>}
        </div>
        <div className="flex flex-wrap gap-1">
          <Link href={href("binary", { offset: binOffset })} onClick={() => select({ kind: "hex", offset: binOffset, length: firstSel !== undefined ? typeSize(s.dataType) : byteLen })}
            className="inline-flex items-center gap-1 rounded border border-border px-1.5 py-0.5 text-[11px] text-calc hover:bg-panel-2">
            <Binary className="size-3" />{t("mapEditor.toBinary")}
          </Link>
          {r.stockSha256 && (
            <Link href={href("diff", { map: s.id })} className="inline-flex items-center gap-1 rounded border border-border px-1.5 py-0.5 text-[11px] text-calc hover:bg-panel-2">
              <GitCompare className="size-3" />{t("mapEditor.toDiff")}
            </Link>
          )}
        </div>
        <div className="divide-y divide-border">
          <KV k={t("maps.address")}><span className="num">{hex(s.address)} … {hex(s.address + byteLen - 1)}</span></KV>
          <KV k={t("maps.size")}>{s.rows} × {s.cols}</KV>
          <KV k={t("maps.dataType")}>{s.dataType} {s.endian === "Big" ? "BE" : "LE"}{w ? ` · ${t(w.order === "row" ? "mapEditor.orderRow" : "mapEditor.orderCol")}` : ""}</KV>
          <KV k={t("maps.factorOffset")}><span className="num">{s.factor} / {s.offset}</span></KV>
          <KV k={t("maps.unit")}>{s.unit}</KV>
          <KV k={t("maps.xAxis")}>{s.xAxis}</KV>
          <KV k={t("maps.yAxis")}>{s.yAxis}</KV>
          <KV k={t("maps.range")}><span className="num">{s.min} … {s.max}</span></KV>
        </div>
        <div className="flex items-start gap-1.5 rounded border border-border bg-panel-2/50 p-2 text-[11px] text-fg-muted">
          <Lock className="mt-0.5 size-3 shrink-0" /><span>{t("mapEditor.axesReadOnly")}</span>
        </div>
        {count > 0 && (
          <div>
            <SectionTitle className="mb-1">{t("mapEditor.selection")}</SectionTitle>
            <div className="num text-fg-muted">
              {t("mapEditor.selected", { n: count })}
              {stats && <> · min {stats.min.toFixed(decimals)} · max {stats.max.toFixed(decimals)} · ⌀ {stats.mean.toFixed(decimals)}</>}
              {count === 1 && firstSel !== undefined && <> · X {data.xAxis[firstSel % cols]} · Y {data.yAxis[Math.floor(firstSel / cols)]} · <b className="text-fg">{values[firstSel].toFixed(decimals)}</b> {s.unit}{stock ? t("maps.cellStock", { v: stock[firstSel].toFixed(decimals) }) : ""}</>}
            </div>
          </div>
        )}
        {history.length > 0 && (
          <div>
            <SectionTitle className="mb-1">{t("mapEditor.history")}</SectionTitle>
            <ul className="space-y-0.5 text-[11px] text-fg-muted">
              {history.slice(-6).reverse().map((h) => <li key={h.id} className="truncate" title={h.description}>· {h.description} <span className="num text-fg-subtle">({h.changedBytes} B)</span></li>)}
            </ul>
          </div>
        )}
        <div>
          <SectionTitle className="mb-1">{t("maps.why")}</SectionTitle>
          <p className="leading-relaxed text-fg-muted">{s.whyItMatters}</p>
        </div>
        {explanation && (
          <div className="space-y-1.5">
            <div className="flex items-center gap-1.5"><SectionTitle>{t("maps.explain")}</SectionTitle><SeverityBadge severity={explanation.severity} /></div>
            <div className="text-fg-muted">{explanation.whatChanged}</div>
            <div className="text-fg-muted">{explanation.assessment}</div>
          </div>
        )}
      </aside>
    </div>
  );
}

function Notice({ tone, children }: { tone: "warn" | "unknown"; children: ReactNode }) {
  return <div className={cn("border-b border-border px-3 py-1.5 text-[11px]", tone === "warn" ? "bg-warn/10 text-warn" : "bg-panel-2 text-fg-muted")}>{children}</div>;
}

/** Raw bytes per cell (working buffer) with absolute addresses; edited cells highlighted; double-click opens Binary. */
const RawHex = memo(function RawHex({ data, w, values, mask, onPick, binaryHref }: {
  data: MapData; w: WorkingMapValues | null; values: number[]; mask: Uint8Array; onPick: (i: number, additive: boolean) => void; binaryHref: (offset: number) => string;
}) {
  const t = useT();
  const { router } = useWorkspace();
  const s = data.summary;
  const cols = data.xAxis.length || 1;
  const size = typeSize(s.dataType);
  const bytesAt = (i: number) => w ? w.bytes[i] : Math.round((values[i] - s.offset) / (s.factor || 1)).toString(16).toUpperCase().padStart(size * 2, "0").slice(-size * 2);
  const addrAt = (i: number) => w ? w.addresses[i] : s.address + i * size;
  return (
    <div className="space-y-2">
      <div className="text-[11px] text-fg-muted">
        {t("maps.raw", { type: s.dataType, endian: s.endian, addr: hex(s.address) })} {w ? t("mapEditor.rawFromWorking") : t("mapEditor.rawComputed")}
      </div>
      <div className="overflow-auto">
        <table className="select-none border-separate border-spacing-0 font-mono text-[10px]">
          <thead>
            <tr>
              <th className="sticky left-0 top-0 z-20 bg-panel-2 px-1.5 py-1 text-[9px] font-normal text-fg-subtle">Y \ X</th>
              {data.xAxis.map((x, c) => <th key={c} className="sticky top-0 z-10 border-b border-border bg-panel-2 px-1.5 py-1 text-right font-medium text-fg-muted">{x}</th>)}
            </tr>
          </thead>
          <tbody>
            {data.yAxis.map((y, r) => (
              <tr key={r}>
                <th className="sticky left-0 z-10 border-r border-border bg-panel-2 px-1.5 text-right font-medium text-fg-muted">{y}</th>
                {data.xAxis.map((_, c) => {
                  const i = r * cols + c;
                  const edited = Math.abs(values[i] - data.values[i]) > 1e-9;
                  return (
                    <td key={c} onMouseDown={(e) => onPick(i, e.ctrlKey || e.metaKey)} onDoubleClick={() => router.push(binaryHref(addrAt(i)))}
                      title={t("mapEditor.rawCellTip", { addr: hex(addrAt(i)) })}
                      className={cn("cursor-pointer border-b border-r border-border/40 px-1.5 py-0.5 text-right", edited && "bg-attn/15", mask[i] && "outline outline-2 -outline-offset-2 outline-calc")}>
                      <div className={cn(edited ? "font-semibold text-attn" : "text-fg")}>{bytesAt(i)}</div>
                      <div className="text-[9px] text-fg-subtle">{hex(addrAt(i), 5)}</div>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
});
