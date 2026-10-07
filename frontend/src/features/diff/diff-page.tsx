"use client";
import { useMemo, useState } from "react";
import Link from "next/link";
import type { AnalysisReport, DiffSummary } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Badge, Card, CardHeader, EmptyState, SeverityBadge } from "@/components/ui";
import { MapViewer } from "@/components/calibration/map-viewer";
import { useMapData, useReport } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";
import { Binary, Table2 } from "lucide-react";
import { ViewSwitch } from "@/components/binary/view-switch";
import { ByteDiff } from "./byte-diff";

export function DiffPage() {
  const t = useT();
  const { analysisId } = useWorkspace();
  const report = useReport();
  const mapsDefault = !!analysisId && (report.isLoading || !!report.data?.stockSha256);
  return (
    <ViewSwitch fallback={mapsDefault ? "maps" : "bytes"} items={[{ value: "maps", label: t("editor.diff.tabMaps"), icon: <Table2 className="size-3.5" /> }, { value: "bytes", label: t("editor.diff.tabBytes"), icon: <Binary className="size-3.5" /> }]}>
      {(view) => (view === "bytes" ? <ByteDiff /> : <WithReport>{(r) => <Diff r={r} />}</WithReport>)}
    </ViewSwitch>
  );
}

function Diff({ r }: { r: AnalysisReport }) {
  const t = useT();
  const { params, setParam, href } = useWorkspace();
  const diffs = useMemo(() => [...(r.modifiedMaps ?? [])].sort((a, b) => Math.abs(b.maxDeltaPct) - Math.abs(a.maxDeltaPct)), [r]);
  const mapId = params.get("map") ?? diffs[0]?.mapId ?? null;
  const data = useMapData(mapId);
  const [sort, setSort] = useState<"delta" | "name">("delta");
  const list = sort === "delta" ? diffs : [...diffs].sort((a, b) => a.name.localeCompare(b.name));

  if (!r.stockSha256) return <div><PageHeader title={t("nav.diff")} /><EmptyState title={t("diff.noStock")}>{t("diff.noStockHint")}</EmptyState></div>;
  return (
    <div className="flex h-full flex-col">
      <PageHeader title={t("nav.diff")} subtitle={t("diff.subtitle", { stock: r.stockName, mod: r.modifiedName, bytes: r.changedBytes, maps: diffs.length, unmapped: (r.unmappedChanges ?? []).length })} />
      <div className="flex min-h-0 flex-1">
        <div className="w-[420px] shrink-0 overflow-y-auto border-r border-border">
          <table className="w-full text-xs">
            <thead className="sticky top-0 bg-panel-2 text-[10px] uppercase tracking-wide text-fg-subtle">
              <tr className="[&>th]:px-2 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium">
                <th className="cursor-pointer" onClick={() => setSort("name")}>{t("diff.colMap")}</th><th>{t("diff.colCells")}</th><th className="cursor-pointer" onClick={() => setSort("delta")}>{t("diff.colMean")}</th><th>{t("diff.colMax")}</th><th title={t("diff.ratioTip")}>{t("diff.colRatio")}</th>
              </tr>
            </thead>
            <tbody>
              {list.map((d) => <Row key={d.mapId} d={d} active={d.mapId === mapId} onClick={() => setParam("map", d.mapId)} />)}
            </tbody>
          </table>
          {(r.unmappedChanges ?? []).length > 0 && (
            <Card className="m-2">
              <CardHeader title={t("diff.outside")} />
              {(r.unmappedChanges ?? []).map((u) => (
                <Link key={u.start} href={href("binary", { offset: u.start })} className="flex items-center gap-2 border-t border-border px-3 py-1.5 text-xs hover:bg-panel-2">
                  <span className="num text-warn">{hex(u.start)}</span><span className="text-fg-muted">{u.length} B</span>
                  <Badge tone={u.section === "Code" ? "danger" : "warn"}>{t.tx(`sectionKind.${u.section}`, u.section)}</Badge>
                  {u.nearestMapId && <span className="truncate text-[11px] text-fg-subtle">{t("diff.near", { id: u.nearestMapId })}</span>}
                </Link>
              ))}
            </Card>
          )}
          <Card className="m-2">
            <CardHeader title={t("diff.ruleFindings")} />
            {r.calibrationFindings.map((f, i) => (
              <div key={i} className="flex items-start gap-2 border-t border-border px-3 py-1.5 text-xs">
                <SeverityBadge severity={f.severity} /><span className="flex-1 text-fg-muted">{f.text}</span>
              </div>
            ))}
          </Card>
        </div>
        <div className="min-w-0 flex-1">
          {data.data ? <MapViewer r={r} data={data.data} initialView="compare" /> : <EmptyState title={t("diff.selectModified")} />}
        </div>
      </div>
    </div>
  );
}

function Row({ d, active, onClick }: { d: DiffSummary; active: boolean; onClick: () => void }) {
  const t = useT();
  const tone = Math.abs(d.maxDeltaPct) > 40 ? "text-danger" : Math.abs(d.maxDeltaPct) > 15 ? "text-warn" : "text-attn";
  return (
    <tr onClick={onClick} className={cn("cursor-pointer border-t border-border hover:bg-panel-2 [&>td]:px-2 [&>td]:py-1.5", active && "bg-calc/12")}>
      <td className="font-medium">{d.name}</td>
      <td className="num text-fg-muted">{d.changedCells}/{d.totalCells}</td>
      <td className={cn("num", tone)}>{d.meanDeltaPct > 0 ? "+" : ""}{d.meanDeltaPct.toFixed(1)}%</td>
      <td className={cn("num", tone)}>{d.maxDeltaPct > 0 ? "+" : ""}{d.maxDeltaPct.toFixed(1)}%</td>
      <td className="num text-fg-subtle" title={t("diff.ratioCellTip")}>{d.meanRatio ? d.meanRatio.toFixed(3) : "—"}</td>
    </tr>
  );
}
