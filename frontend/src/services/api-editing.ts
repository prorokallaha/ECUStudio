import type { EditState, HexEdit, RevertRequest, SaveCheck, SaveRequest, SaveResult, WorkingHexDto } from "@/types/editing";
import { API_BASE, ApiError, request } from "./api";

/** Max bytes per working-hex request (backend limit). */
export const WORKING_HEX_MAX = 64 * 1024;

const base = (projectId: string, fileId: string) => `/projects/${projectId}/files/${fileId}`;

/**
 * Safe binary editing API. All edits go through the backend patch model: the stored original is never modified,
 * saving always produces a new project file.
 */
export const editingApi = {
  /** URL of the raw stored file (the original bytes of that file, never the unsaved working buffer). */
  contentUrl: (projectId: string, fileId: string) => `${API_BASE}/api/v1${base(projectId, fileId)}/content`,

  /** Downloads the raw stored file into a typed array (files are ≤ 16 MB). */
  async content(projectId: string, fileId: string, signal?: AbortSignal): Promise<Uint8Array> {
    let res: Response;
    try {
      res = await fetch(editingApi.contentUrl(projectId, fileId), { signal });
    } catch (e) {
      if ((e as Error)?.name === "AbortError") throw e;
      throw new ApiError(0, "NETWORK", "ECUStudio engine is not reachable");
    }
    if (!res.ok) throw new ApiError(res.status, `HTTP_${res.status}`, res.statusText);
    return new Uint8Array(await res.arrayBuffer());
  },

  state: (projectId: string, fileId: string) => request<EditState>(`${base(projectId, fileId)}/edits`),
  hex: (projectId: string, fileId: string, offset: number, length: number) =>
    request<WorkingHexDto>(`${base(projectId, fileId)}/edits/hex?offset=${offset}&length=${Math.min(length, WORKING_HEX_MAX)}`),
  writeHex: (projectId: string, fileId: string, body: HexEdit) => request<EditState>(`${base(projectId, fileId)}/edits/hex`, { method: "POST", json: body }),
  undo: (projectId: string, fileId: string) => request<EditState>(`${base(projectId, fileId)}/edits/undo`, { method: "POST" }),
  redo: (projectId: string, fileId: string) => request<EditState>(`${base(projectId, fileId)}/edits/redo`, { method: "POST" }),
  revert: (projectId: string, fileId: string, body: Partial<RevertRequest>) =>
    request<EditState>(`${base(projectId, fileId)}/edits/revert`, { method: "POST", json: { all: false, ...body } }),
  saveCheck: (projectId: string, fileId: string, body: SaveRequest) => request<SaveCheck>(`${base(projectId, fileId)}/edits/save-check`, { method: "POST", json: body }),
  save: (projectId: string, fileId: string, body: SaveRequest) => request<SaveResult>(`${base(projectId, fileId)}/edits/save`, { method: "POST", json: body }),
};
