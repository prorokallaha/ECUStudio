"use client";
import { useCallback, useMemo } from "react";
import { Panel, PanelGroup, PanelResizeHandle } from "react-resizable-panels";
import { Map as MapIcon } from "lucide-react";
import type { AnalysisReport } from "@/types/domain";
import type { MapEditorView } from "@/types/maps-edit";
import { WithReport } from "@/components/layout/page";
import { EmptyState, Skeleton } from "@/components/ui";
import { MapTree } from "@/components/calibration/map-tree";
import { MapEditor } from "@/components/calibration/map-editor";
import { CandidatePanel } from "@/components/calibration/candidate-panel";
import { useMapData } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useSelection } from "@/stores/selection";
import { editedMapIds, useEditState, useEditTarget } from "./use-map-edits";
import { useT } from "@/i18n";

/** Legacy `view` values (map-viewer) map onto the editor tabs unchanged. */
export type MapView = MapEditorView;

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
  const target = useEditTarget(r);
  const editState = useEditState(target);
  const edited = useMemo(() => editedMapIds(editState.data), [editState.data]);

  const onMap = useCallback((id: string) => { setParam("candidate", null); setParam("map", id); }, [setParam]);
  const onView = useCallback((v: MapView) => setParam("view", v === "table" ? null : v), [setParam]);
  const onCandidate = useCallback((id: string) => { setParam("map", null); setParam("candidate", id); select({ kind: "candidate", candidateId: id }); }, [setParam, select]);

  return (
    <PanelGroup direction="horizontal" autoSaveId="ecustudio.maps" className="h-full">
      <Panel defaultSize={20} minSize={14} maxSize={35} className="border-r border-border bg-bg-elev">
        <MapTree r={r} edited={edited} activeMap={candidateId ? null : mapId} activeCandidate={candidateId} onMap={onMap} onCandidate={onCandidate} />
      </Panel>
      <PanelResizeHandle className="w-px bg-border hover:bg-calc" />
      <Panel minSize={40}>
        {candidate ? <div className="h-full overflow-auto"><CandidatePanel r={r} c={candidate} /></div>
          : data.isLoading ? <div className="p-4"><Skeleton className="h-80" /></div>
          : data.data ? <MapEditor r={r} data={data.data} target={target} editState={editState.data} initialView={view} onViewChange={onView} />
          : <EmptyState icon={<MapIcon className="size-8" />} title={t("maps.selectMap")}>{t("maps.selectHint")}</EmptyState>}
      </Panel>
    </PanelGroup>
  );
}

export { Maps };
