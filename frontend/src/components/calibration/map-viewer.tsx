"use client";
import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { Binary, Box, Grid3x3, LineChart, Rows3, SplitSquareHorizontal, Table2 } from "lucide-react";
import type { AnalysisReport, MapData } from "@/types/domain";
import { Badge, ConfidenceBadge, KV, SectionTitle, Segmented, SeverityBadge, SourceBadge, Tabs } from "@/components/ui";
import { MapGrid, type CellRef } from "./map-grid";
import { Map2D, Map3D, MapHeatmap } from "./map-charts";
import { useSelection } from "@/stores/selection";
import { useWorkspace } from "@/hooks/use-workspace";
import { hex } from "@/lib/format";

export type MapView = "table" | "2d" | "3d" | "heatmap" | "hex" | "compare";

/** Synchronized views of one map: the selected cell is shared by table, 2D, heatmap and compare. */
export function MapViewer({ r, data, initialView = "table" }: { r: AnalysisReport; data: MapData; initialView?: MapView }) {
  const { href } = useWorkspace();
  const [view, setView] = useState<MapView>(initialView);
  const [sel, setSel] = useState<CellRef[]>([]);
  const [onlyChanged, setOnlyChanged] = useState(false);
  const [deltaKind, setDeltaKind] = useState<"delta" | "deltaPct">("deltaPct");
  const select = useSelection((s) => s.select);
  const s = data.summary;
  const hasStock = !!data.stockValues;
  const sameAxes = hasStock && JSON.stringify(data.stockXAxis) === JSON.stringify(data.xAxis) && JSON.stringify(data.stockYAxis) === JSON.stringify(data.yAxis);
  const explanation = r.explanations?.find((e) => e.mapId === s.id);
  const diff = r.modifiedMaps?.find((d) => d.mapId === s.id);
  const nodeIds = new Set(r.dependencies.nodes.filter((n) => n.mapId === s.id).map((n) => n.id));
  const deps = r.dependencies.edges.filter((e) => nodeIds.has(e.from) || nodeIds.has(e.to)).length;

  useEffect(() => { setSel([]); }, [data.id]);
  useEffect(() => { setView(initialView); }, [initialView]);

  const onSelect = (cells: CellRef[], additive: boolean) => {
    const next = additive ? [...sel.filter((c) => !cells.some((n) => n.row === c.row && n.col === c.col)), ...cells] : cells;
    setSel(next);
    const cols = data.xAxis.length;
    select({
      kind: "map", mapId: s.id, mapName: s.name, unit: s.unit,
      cells: next.slice(0, 64).map((c) => ({ row: c.row, col: c.col, x: data.xAxis[c.col], y: data.yAxis[c.row], mod: data.values[c.row * cols + c.col], stock: data.stockValues?.[c.row * cols + c.col] ?? null })),
    });
  };

  const d = useMemo(() => ({ xAxis: data.xAxis, yAxis: data.yAxis, values: data.values, stock: sameAxes ? data.stockValues : null, unit: s.unit, xName: s.xAxis.split(" ")[0], yName: s.yAxis.split(" ")[0] }), [data, sameAxes, s]);
  const cell = sel[0];
  const decimals = Math.max(0, Math.min(4, Math.ceil(-Math.log10(s.factor || 1) - 1e-9)));
  const cols = data.xAxis.length;

  return (
    <div className="flex h-full min-h-0">
      <div className="flex min-w-0 flex-1 flex-col">
        <Tabs
          value={view}
          onChange={setView}
          items={[
            { value: "table", label: "Table", icon: <Table2 className="size-3.5" /> },
            { value: "2d", label: "2D", icon: <LineChart className="size-3.5" /> },
            { value: "3d", label: "3D", icon: <Box className="size-3.5" />, hidden: data.yAxis.length < 2 },
            { value: "heatmap", label: "Heatmap", icon: <Grid3x3 className="size-3.5" /> },
            { value: "hex", label: "Hex", icon: <Binary className="size-3.5" /> },
            { value: "compare", label: "Compare", icon: <SplitSquareHorizontal className="size-3.5" />, hidden: !hasStock },
          ]}
          right={view === "compare" ? (
            <>
              <Segmented size="xs" value={deltaKind} onChange={setDeltaKind} options={[{ value: "deltaPct", label: "Δ%" }, { value: "delta", label: "Δ abs" }]} />
              <label className="flex items-center gap-1 whitespace-nowrap text-[11px] text-fg-muted"><input type="checkbox" checked={onlyChanged} onChange={(e) => setOnlyChanged(e.target.checked)} />Only changed</label>
            </>
          ) : cell ? (
            <span className="num text-[11px] text-fg-muted">X {data.xAxis[cell.col]} · Y {data.yAxis[cell.row]} · <b className="text-fg">{data.values[cell.row * cols + cell.col]}</b> {s.unit}{hasStock && sameAxes ? ` (stock ${data.stockValues![cell.row * cols + cell.col]})` : ""}</span>
          ) : null}
        />
        <div className="min-h-0 flex-1 overflow-auto p-3">
          {view === "table" && <MapGrid xAxis={data.xAxis} yAxis={data.yAxis} values={data.values} stock={d.stock} kind="values" unit={s.unit} selected={sel} onSelect={onSelect} decimals={decimals} />}
          {view === "2d" && <div className="h-full min-h-80"><Map2D d={d} selected={sel} onPick={(c) => onSelect([c], false)} /></div>}
          {view === "3d" && <div className="h-full min-h-96"><Map3D d={d} /></div>}
          {view === "heatmap" && (
            <div className="flex h-full min-h-80 flex-col gap-2">
              <div className="min-h-0 flex-1"><MapHeatmap d={d} onPick={(c) => onSelect([c], false)} /></div>
              {d.stock && <div className="min-h-0 flex-1"><MapHeatmap d={d} delta onPick={(c) => onSelect([c], false)} /></div>}
            </div>
          )}
          {view === "hex" && <RawView data={data} />}
          {view === "compare" && hasStock && (
            sameAxes ? (
              <div className="@container"><div className="grid grid-cols-1 gap-3 @5xl:grid-cols-3">
                {(["stock", "values", deltaKind] as const).map((k) => (
                  <div key={k} className="min-w-0 rounded-md border border-border">
                    <div className="border-b border-border px-2 py-1 text-[10px] font-semibold uppercase tracking-wider text-fg-subtle">{k === "stock" ? "Stock" : k === "values" ? "Modified" : "Delta"}</div>
                    <MapGrid xAxis={data.xAxis} yAxis={data.yAxis} values={data.values} stock={data.stockValues} kind={k} selected={sel} onSelect={onSelect} onlyChanged={onlyChanged} dense decimals={k === "deltaPct" ? undefined : decimals} />
                  </div>
                ))}
              </div></div>
            ) : (
              <div className="space-y-2">
                <Badge tone="warn">Axes changed</Badge>
                <div className="text-xs text-fg-muted">Breakpoints differ between stock and modified; cell-by-cell delta would compare different operating points. Showing both tables separately.</div>
                <MapGrid xAxis={data.stockXAxis ?? []} yAxis={data.stockYAxis ?? []} values={data.stockValues ?? []} kind="values" selected={[]} onSelect={() => {}} dense />
                <MapGrid xAxis={data.xAxis} yAxis={data.yAxis} values={data.values} kind="values" selected={sel} onSelect={onSelect} dense />
              </div>
            )
          )}
        </div>
      </div>

      <aside className="w-72 shrink-0 space-y-3 overflow-y-auto border-l border-border bg-bg-elev p-3 text-xs">
        <div>
          <div className="text-sm font-semibold">{s.name}</div>
          <div className="mt-1 flex flex-wrap gap-1"><SourceBadge source={s.source} /><ConfidenceBadge score={s.confidence} />{s.modified ? <Badge tone="calc">modified</Badge> : <Badge tone="ok">stock</Badge>}</div>
        </div>
        <div className="divide-y divide-border">
          <KV k="Address"><Link className="num text-calc hover:underline" href={href("binary", { offset: s.address })}>{hex(s.address)}</Link></KV>
          <KV k="Size">{s.rows} × {s.cols}</KV>
          <KV k="Data type">{s.dataType} {s.endian === "Big" ? "BE" : "LE"}</KV>
          <KV k="Factor / offset"><span className="num">{s.factor} / {s.offset}</span></KV>
          <KV k="Unit">{s.unit}</KV>
          <KV k="X axis">{s.xAxis}</KV>
          <KV k="Y axis">{s.yAxis}</KV>
          <KV k="Range"><span className="num">{s.min} … {s.max}</span></KV>
          <KV k="Dependencies"><Link className="text-calc hover:underline" href={href("dependencies", { focus: [...nodeIds][0] })}>{deps} links</Link></KV>
          {diff && <KV k="Changed cells"><span className="num">{diff.changedCells}/{diff.totalCells} · mean {diff.meanDeltaPct.toFixed(1)}%</span></KV>}
        </div>
        <div>
          <SectionTitle className="mb-1">Why does this map matter?</SectionTitle>
          <p className="leading-relaxed text-fg-muted">{s.whyItMatters}</p>
        </div>
        {explanation && (
          <div className="space-y-1.5">
            <div className="flex items-center gap-1.5"><SectionTitle>Explain</SectionTitle><SeverityBadge severity={explanation.severity} /></div>
            <Ex k="What changed" v={explanation.whatChanged} />
            <Ex k="Physical effect" v={explanation.physicalEffect} />
            <Ex k="Assessment" v={explanation.assessment} />
            {explanation.linkedTo.length > 0 && <Ex k="Linked to" v={explanation.linkedTo.join(", ")} />}
            {explanation.dataToIncreaseConfidence.length > 0 && <Ex k="To raise confidence" v={explanation.dataToIncreaseConfidence.join("; ")} />}
          </div>
        )}
      </aside>
    </div>
  );
}

function Ex({ k, v }: { k: string; v: string }) {
  return <div><div className="text-[10px] uppercase tracking-wide text-fg-subtle">{k}</div><div className="text-fg-muted">{v}</div></div>;
}

function RawView({ data }: { data: MapData }) {
  const s = data.summary;
  const size = s.dataType.includes("32") ? 4 : s.dataType.includes("8") ? 1 : 2;
  const cols = data.xAxis.length;
  return (
    <div className="space-y-2">
      <div className="text-[11px] text-fg-muted">Raw = (value − offset) / factor, {s.dataType} {s.endian}-endian, starting at {hex(s.address)}.</div>
      <div className="overflow-auto font-mono text-[11px]">
        {data.yAxis.map((_, r) => (
          <div key={r} className="flex gap-3 whitespace-pre">
            <span className="text-fg-subtle">{hex(s.address + r * cols * size)}</span>
            <span>{data.xAxis.map((_, c) => Math.round((data.values[r * cols + c] - s.offset) / (s.factor || 1)).toString(16).toUpperCase().padStart(size * 2, "0")).join(" ")}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
