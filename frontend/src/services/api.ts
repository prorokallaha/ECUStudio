import type {
  ProjectLog,
  AIAnalysisResult, AnalysisReport, AssistantAnswer, ComponentKind, ComponentSpec, DataType, DynoRequest, DynoResult,
  Endianness, FileRole, HardwareOverride, HexPageDto, JobEvent, MapData, MapHypothesisResult, CandidateData, PointInspection, Project,
  ProjectFile, SystemInfo, VehicleVariant, VinInfo,
} from "@/types/domain";

/** Same-origin in production (served by the .NET host); NEXT_PUBLIC_API_BASE in `next dev`. */
export const API_BASE = (process.env.NEXT_PUBLIC_API_BASE ?? "").replace(/\/$/, "");

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public details?: unknown) {
    super(message);
  }
}

export async function request<T>(path: string, init?: RequestInit & { json?: unknown }): Promise<T> {
  const headers = new Headers(init?.headers);
  let body = init?.body;
  if (init?.json !== undefined) {
    headers.set("content-type", "application/json");
    body = JSON.stringify(init.json);
  }
  let res: Response;
  try {
    res = await fetch(`${API_BASE}/api/v1${path}`, { ...init, headers, body });
  } catch {
    throw new ApiError(0, "NETWORK", "ECUStudio engine is not reachable");
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  const data = text ? safeJson(text) : undefined;
  if (!res.ok) {
    const err = (data as { error?: { code?: string; message?: string; details?: unknown } } | undefined)?.error;
    throw new ApiError(res.status, err?.code ?? `HTTP_${res.status}`, err?.message ?? res.statusText, err?.details);
  }
  return (data ?? text) as T;
}

function safeJson(text: string): unknown {
  try { return JSON.parse(text); } catch { return text; }
}

export const api = {
  info: () => request<SystemInfo>("/info"),
  decodeVin: (vin: string) => request<VinInfo>(`/vin/${encodeURIComponent(vin)}`),
  components: (kind?: ComponentKind) => request<ComponentSpec[]>(`/components${kind ? `?kind=${kind}` : ""}`),
  variants: () => request<VehicleVariant[]>("/variants"),

  projects: {
    list: () => request<Project[]>("/projects/"),
    get: (id: string) => request<Project>(`/projects/${id}`),
    create: (body: { name: string; vin?: string | null; notes?: string | null }) => request<Project>("/projects/", { method: "POST", json: body }),
    demo: () => request<Project>("/projects/demo", { method: "POST" }),
    update: (id: string, body: Partial<Pick<Project, "name" | "vin" | "notes" | "preferredVariantId" | "transmissionId">>) =>
      request<Project>(`/projects/${id}`, { method: "PATCH", json: body }),
    remove: (id: string) => request<void>(`/projects/${id}`, { method: "DELETE" }),
    upload: (id: string, file: File, role?: FileRole, label?: string) => {
      const form = new FormData();
      form.append("file", file);
      if (role) form.append("role", role);
      if (label) form.append("label", label);
      return request<ProjectFile>(`/projects/${id}/files`, { method: "POST", body: form });
    },
    uploadLog: (id: string, file: File, fileId?: string) => {
      const form = new FormData();
      form.append("file", file);
      if (fileId) form.append("fileId", fileId);
      return request<ProjectLog>(`/projects/${id}/logs`, { method: "POST", body: form });
    },
    deleteLog: (id: string, logId: string) => request<Project>(`/projects/${id}/logs/${logId}`, { method: "DELETE" }),
    setFileRole: (id: string, fileId: string, role: FileRole) => request<Project>(`/projects/${id}/files/${fileId}/role`, { method: "PUT", json: { role } }),
    setHardware: (id: string, overrides: HardwareOverride[]) => request<Project>(`/projects/${id}/hardware`, { method: "PUT", json: { overrides } }),
    resetHardware: (id: string, kind: ComponentKind) => request<Project>(`/projects/${id}/hardware/${kind}`, { method: "DELETE" }),
    analyze: (id: string, body?: { modifiedFileId?: string; stockFileId?: string }) =>
      request<{ jobId: string }>(`/projects/${id}/analyses`, { method: "POST", json: body ?? {} }),
  },

  jobs: {
    get: (id: string) => request<{ id: string; status: string; events: JobEvent[] }>(`/jobs/${id}`),
    /** Server-Sent Events stream with history replay. Returns an unsubscribe function. */
    subscribe(id: string, onEvent: (e: JobEvent) => void, onError?: () => void): () => void {
      const es = new EventSource(`${API_BASE}/api/v1/jobs/${id}/events`);
      es.addEventListener("job", (m) => {
        const e = JSON.parse((m as MessageEvent).data) as JobEvent;
        onEvent(e);
        if (e.status === "Completed" || e.status === "Failed") es.close();
      });
      es.onerror = () => { es.close(); onError?.(); };
      return () => es.close();
    },
  },

  analyses: {
    report: (id: string) => request<AnalysisReport>(`/analyses/${id}/`),
    reportMarkdownUrl: (id: string) => `${API_BASE}/api/v1/analyses/${id}/report.md`,
    map: (id: string, mapId: string) => request<MapData>(`/analyses/${id}/maps/${encodeURIComponent(mapId)}`),
    hex: (id: string, offset: number, length: number) => request<HexPageDto>(`/analyses/${id}/hex?offset=${offset}&length=${length}`),
    search: (id: string, mode: "hex" | "ascii" | "int" | "float", q: string, type?: DataType, endian?: Endianness) =>
      request<number[]>(`/analyses/${id}/search?mode=${mode}&q=${encodeURIComponent(q)}${type ? `&type=${type}` : ""}${endian ? `&endian=${endian}` : ""}`),
    dyno: (id: string, body: Partial<DynoRequest>) => request<DynoResult>(`/analyses/${id}/dyno`, { method: "POST", json: body }),
    inspect: (id: string, body: { rpm: number; pedalPct: number; gear?: number; ambientTempC?: number; altitudeM?: number }) =>
      request<PointInspection>(`/analyses/${id}/inspect`, { method: "POST", json: body }),
    decide: (id: string, candidateId: string, body: { decision: "confirm" | "reject"; role?: string; note?: string }) =>
      request<void>(`/analyses/${id}/candidates/${encodeURIComponent(candidateId)}/decision`, { method: "POST", json: body }),
    candidateData: (id: string, candidateId: string) => request<CandidateData>(`/analyses/${id}/candidates/${encodeURIComponent(candidateId)}/data`),
    hypotheses: (id: string, candidateId: string) => request<MapHypothesisResult[]>(`/analyses/${id}/candidates/${encodeURIComponent(candidateId)}/hypotheses`, { method: "POST" }),
    startAI: (id: string) => request<{ jobId: string }>(`/analyses/${id}/ai`, { method: "POST" }),
    aiResult: (id: string) => request<AIAnalysisResult>(`/analyses/${id}/ai`),
    ask: (id: string, question: string, selection?: unknown) => request<AssistantAnswer>(`/analyses/${id}/ai/ask`, { method: "POST", json: { question, selection } }),
  },
};

export function base64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
