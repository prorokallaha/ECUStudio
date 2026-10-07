"use client";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { api, ApiError } from "@/services/api";
import { useJobs } from "@/stores/jobs";
import { useWorkspace } from "./use-workspace";

export const keys = {
  projects: ["projects"] as const,
  project: (id: string) => ["project", id] as const,
  report: (id: string) => ["report", id] as const,
  map: (a: string, m: string) => ["map", a, m] as const,
  info: ["info"] as const,
};

export function useInfo() {
  return useQuery({ queryKey: keys.info, queryFn: api.info, staleTime: 60_000 });
}

export function useProject(id?: string | null) {
  return useQuery({ queryKey: keys.project(id ?? ""), queryFn: () => api.projects.get(id!), enabled: !!id });
}

/** The current analysis report. Reports are immutable, so they are cached forever. */
export function useReport() {
  const { analysisId } = useWorkspace();
  return useQuery({ queryKey: keys.report(analysisId ?? ""), queryFn: () => api.analyses.report(analysisId!), enabled: !!analysisId, staleTime: Infinity });
}

export function useMapData(mapId?: string | null) {
  const { analysisId } = useWorkspace();
  return useQuery({ queryKey: keys.map(analysisId ?? "", mapId ?? ""), queryFn: () => api.analyses.map(analysisId!, mapId!), enabled: !!analysisId && !!mapId, staleTime: Infinity });
}

/** Starts an analysis and follows its progress over SSE; on completion switches the workspace to the new analysis. */
export function useRunAnalysis() {
  const qc = useQueryClient();
  const { router, href } = useWorkspace();
  const track = useJobs((s) => s.track);
  const apply = useJobs((s) => s.apply);
  return useMutation({
    mutationFn: async ({ projectId, modifiedFileId, stockFileId, navigate = true }: { projectId: string; modifiedFileId?: string; stockFileId?: string; navigate?: boolean }) => {
      const { jobId } = await api.projects.analyze(projectId, { modifiedFileId, stockFileId });
      track({ jobId, kind: "analysis", projectId });
      return await new Promise<string>((resolve, reject) => {
        api.jobs.subscribe(jobId, (e) => {
          apply(e);
          if (e.status === "Completed" && e.analysisId) resolve(e.analysisId);
          if (e.status === "Failed") reject(new ApiError(422, "ANALYSIS_FAILED", e.error ?? "Analysis failed"));
        }, () => reject(new ApiError(0, "SSE", "Lost connection to progress stream")));
      }).then((analysisId) => {
        qc.invalidateQueries({ queryKey: keys.project(projectId) });
        qc.invalidateQueries({ queryKey: keys.projects });
        if (navigate) {
          const target = new URL(href("dashboard"), window.location.origin);
          target.searchParams.set("p", projectId);
          target.searchParams.set("a", analysisId);
          router.push(target.pathname + target.search);
        }
        return analysisId;
      });
    },
    onError: (e) => toast.error(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e)),
  });
}
