import type { MapData } from "@/types/domain";
import type { EditState, MapEditPreview, MapOperation, WorkingHexDto } from "@/types/maps-edit";
import { request } from "./api";

const enc = encodeURIComponent;
const edits = (projectId: string, fileId: string) => `/projects/${projectId}/files/${fileId}/edits`;

/** Maps workspace: map data, working-buffer bytes and map edits through the patch model (never direct writes). */
export const mapsApi = {
  map: (analysisId: string, mapId: string) => request<MapData>(`/analyses/${analysisId}/maps/${enc(mapId)}`),

  /** Bytes of the working buffer (after applied patches) and of the original file. */
  workingHex: (projectId: string, fileId: string, offset: number, length: number) =>
    request<WorkingHexDto>(`${edits(projectId, fileId)}/hex?offset=${offset}&length=${length}`),

  state: (projectId: string, fileId: string) => request<EditState>(edits(projectId, fileId)),

  /** Computes the byte writes of an operation without applying anything. */
  preview: (projectId: string, fileId: string, mapId: string, operation: MapOperation) =>
    request<MapEditPreview>(`${edits(projectId, fileId)}/map/preview`, { method: "POST", json: { mapId, operation } }),

  /** Applies an operation as one undoable patch. */
  apply: (projectId: string, fileId: string, mapId: string, operation: MapOperation, description?: string) =>
    request<EditState>(`${edits(projectId, fileId)}/map`, { method: "POST", json: { mapId, operation, description } }),

  revert: (projectId: string, fileId: string, mapId: string) =>
    request<EditState>(`${edits(projectId, fileId)}/revert`, { method: "POST", json: { mapId } }),
  undo: (projectId: string, fileId: string) => request<EditState>(`${edits(projectId, fileId)}/undo`, { method: "POST" }),
  redo: (projectId: string, fileId: string) => request<EditState>(`${edits(projectId, fileId)}/redo`, { method: "POST" }),
};
