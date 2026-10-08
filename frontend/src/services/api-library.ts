import type { Project } from "@/types/domain";
import type { AcquisitionSettings, DefinitionAcquisition, DefinitionMatch, DefinitionPreview, LibraryEntryQuery, LibraryRoot, LibraryRootStatus, LibrarySearchResult, TorrentSourcesDto } from "@/types/library";
import { request } from "./api";

/** Definition library (indexed in place, never copied) and the project definition binding. */
export const libraryApi = {
  roots: () => request<LibraryRootStatus[]>("/library/roots"),
  addRoot: (body: { path: string; name?: string | null }) => request<LibraryRoot>("/library/roots", { method: "POST", json: body }),
  addTorrent: (file: File, downloadPath?: string | null) => {
    const form = new FormData();
    form.append("file", file);
    if (downloadPath) form.append("downloadPath", downloadPath);
    return request<LibraryRoot>("/library/torrents", { method: "POST", body: form });
  },
  updateRoot: (rootId: string, body: { name?: string | null; downloadPath?: string | null }) =>
    request<LibraryRoot>(`/library/roots/${rootId}`, { method: "PATCH", json: body }),
  torrents: () => request<TorrentSourcesDto>("/library/torrents"),
  addMagnet: (body: { uri: string; name?: string | null }) => request<LibraryRoot>("/library/torrents/magnet", { method: "POST", json: body }),
  setTorrentSource: (rootId: string, body: { enabled?: boolean | null; priority?: number | null }) =>
    request<LibraryRoot>(`/library/torrents/${rootId}`, { method: "PATCH", json: { enabled: body.enabled ?? null, priority: body.priority ?? null } }),
  removeRoot: (rootId: string) => request<void>(`/library/roots/${rootId}`, { method: "DELETE" }),
  scan: (rootId: string) => request<unknown>(`/library/roots/${rootId}/scan`, { method: "POST" }),
  entries: ({ q, format, definitionsOnly, offset, limit }: LibraryEntryQuery) => {
    const p = new URLSearchParams();
    if (q) p.set("q", q);
    if (format) p.set("format", format);
    if (definitionsOnly) p.set("definitionsOnly", "true");
    if (offset) p.set("offset", String(offset));
    if (limit) p.set("limit", String(limit));
    const qs = p.toString();
    return request<LibrarySearchResult>(`/library/entries${qs ? `?${qs}` : ""}`);
  },
  matches: (analysisId: string) => request<DefinitionMatch[]>(`/analyses/${analysisId}/definitions`),

  acquisition: {
    /** Null when no search has run for the project yet. */
    status: (projectId: string) => request<DefinitionAcquisition | undefined>(`/projects/${projectId}/definition/acquisition`).then((r) => r ?? null),
    start: (projectId: string, body?: { fileId?: string | null; entryId?: string | null }) =>
      request<{ jobId: string }>(`/projects/${projectId}/definition/acquisition`, { method: "POST", json: body ?? {} }),
    settings: () => request<AcquisitionSettings>("/acquisition/settings"),
    saveSettings: (body: AcquisitionSettings) => request<AcquisitionSettings>("/acquisition/settings", { method: "PUT", json: body }),
  },

  definition: {
    preview: (projectId: string, file: File) => {
      const form = new FormData();
      form.append("file", file);
      return request<DefinitionPreview>(`/projects/${projectId}/definition/preview`, { method: "POST", body: form });
    },
    bind: (projectId: string, file: File, force = false) => {
      const form = new FormData();
      form.append("file", file);
      form.append("force", force ? "true" : "false");
      return request<Project>(`/projects/${projectId}/definition`, { method: "POST", body: form });
    },
    previewLibrary: (projectId: string, entryId: string) =>
      request<DefinitionPreview>(`/projects/${projectId}/definition/library/preview`, { method: "POST", json: { entryId } }),
    bindLibrary: (projectId: string, entryId: string, force = false) =>
      request<Project>(`/projects/${projectId}/definition/library`, { method: "POST", json: { entryId, force } }),
    unbind: (projectId: string) => request<Project>(`/projects/${projectId}/definition`, { method: "DELETE" }),
  },
};
