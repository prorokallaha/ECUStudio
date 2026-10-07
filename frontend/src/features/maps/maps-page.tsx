"use client";
import { Panel, PanelGroup, PanelResizeHandle } from "react-resizable-panels";
import { Map as MapIcon } from "lucide-react";
import type { AnalysisReport } from "@/types/domain";
import { WithReport } from "@/components/layout/page";
import { EmptyState, Skeleton } from "@/components/ui";
import { MapBrowser } from "@/components/calibration/map-browser";
import { MapViewer, type MapView } from "@/components/calibration/map-viewer";
import { CandidatePanel } from "@/components/calibration/candidate-panel";
import { useMapData } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useSelection } from "@/stores/selection";
import { useT } from "@/i18n";

export function MapsPage() {
  return <WithReport>{(r) => <Maps r={r} />}</WithReport>;
}

function Maps({ r, defaultView }: { r: AnalysisReport; defaultView?: MapView }) {
  const t = useT();
  const { params, setParam } = useWorkspace();
  const select = useSelection((s) => s.select);
  const mapId = params.get("map") ?? (params.get("candidate") ? null : r.maps.find((m) => m.modified)?.id ?? r.maps[0]?.id ?? null);
  const candidateId = params.get("candidate");
  const data = useMapData(candidateId ? null : mapId);
  const candidate = candidateId ? r.candidates.find((c) => c.id === candidateId) : undefined;
  const view = (params.get("view") as MapView | null) ?? defaultView ?? "table";

  return (
    <PanelGroup direction="horizontal" autoSaveId="ecustudio.maps" className="h-full">
      <Panel defaultSize={20} minSize={14} maxSize={35} className="border-r border-border bg-bg-elev">
        <MapBrowser r={r} activeMap={candidateId ? null : mapId} activeCandidate={candidateId}
          onMap={(id) => { setParam("candidate", null); setParam("map", id); }}
          onCandidate={(id) => { setParam("map", null); setParam("candidate", id); select({ kind: "candidate", candidateId: id }); }} />
      </Panel>
      <PanelResizeHandle className="w-px bg-border hover:bg-calc" />
      <Panel minSize={40}>
        {candidate ? <div className="h-full overflow-auto"><CandidatePanel r={r} c={candidate} /></div>
          : data.isLoading ? <div className="p-4"><Skeleton className="h-80" /></div>
          : data.data ? <MapViewer r={r} data={data.data} initialView={view} />
          : <EmptyState icon={<MapIcon className="size-8" />} title={t("maps.selectMap")}>{t("maps.selectHint")}</EmptyState>}
      </Panel>
    </PanelGroup>
  );
}

export { Maps };
