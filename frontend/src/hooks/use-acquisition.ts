"use client";
import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { libraryApi } from "@/services/api-library";
import { api } from "@/services/api";
import type { AcquisitionSettings, DefinitionAcquisition, TransferInfo } from "@/types/library";
import type { StepProgress } from "@/types/domain";
import { errorText, libraryKeys } from "./use-library";

export const acqKeys = {
  status: (projectId: string) => ["acquisition", projectId] as const,
  all: ["acquisition"] as const,
  settings: ["acquisition-settings"] as const,
  torrents: ["library", "torrents"] as const,
};

const ACTIVE = new Set<DefinitionAcquisition["state"]>(["Identify", "LocalSearch", "TorrentSearch", "WaitingMetadata", "Downloading", "Verifying", "Importing", "MatchingMaps"]);
export const isAcquiring = (a?: DefinitionAcquisition | null) => !!a && ACTIVE.has(a.state);

export interface AcquisitionLive {
  steps: StepProgress[];
  transfer?: TransferInfo | null;
}

/**
 * Definition search of a project. The stored state is polled while a search runs; stages and transfer numbers come
 * live from the job's SSE stream.
 */
export function useAcquisition(projectId?: string | null) {
  const qc = useQueryClient();
  const query = useQuery({
    queryKey: acqKeys.status(projectId ?? ""),
    queryFn: () => libraryApi.acquisition.status(projectId!),
    enabled: !!projectId,
    refetchInterval: (q) => (isAcquiring(q.state.data) ? 2000 : false),
  });
  const [live, setLive] = useState<AcquisitionLive>({ steps: [] });
  const jobId = isAcquiring(query.data) ? query.data?.jobId : null;
  useEffect(() => {
    if (!jobId || !projectId) return;
    setLive({ steps: [] });
    return api.jobs.subscribe(jobId, (e) => {
      setLive((l) => {
        const steps = [...l.steps];
        if (e.step) {
          const i = steps.findIndex((s) => s.step === e.step!.step);
          if (i >= 0) steps[i] = e.step; else steps.push(e.step);
        }
        return { steps, transfer: e.transfer ?? l.transfer };
      });
      if ((e.step && e.step.state !== "Running") || e.status === "Completed" || e.status === "Failed")
        qc.invalidateQueries({ queryKey: acqKeys.status(projectId) });
    });
  }, [jobId, projectId, qc]);
  return { ...query, live };
}

/** Starts a search, or downloads and checks one candidate the user picked. */
export function useStartAcquisition(projectId?: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (entryId?: string | null) => libraryApi.acquisition.start(projectId!, { entryId: entryId ?? null }),
    onSuccess: () => qc.invalidateQueries({ queryKey: acqKeys.status(projectId ?? "") }),
    onError: (e) => toast.error(errorText(e)),
  });
}

export function useAcquisitionSettings() {
  const qc = useQueryClient();
  const query = useQuery({ queryKey: acqKeys.settings, queryFn: libraryApi.acquisition.settings });
  const save = useMutation({
    mutationFn: (s: AcquisitionSettings) => libraryApi.acquisition.saveSettings(s),
    onSuccess: (s) => qc.setQueryData(acqKeys.settings, s),
    onError: (e) => toast.error(errorText(e)),
  });
  return { ...query, save };
}

export function useTorrentSources() {
  const qc = useQueryClient();
  const query = useQuery({ queryKey: acqKeys.torrents, queryFn: libraryApi.torrents, refetchInterval: (q) => (q.state.data?.sources.some((s) => !s.indexed) ? 2000 : false) });
  const refresh = () => {
    qc.invalidateQueries({ queryKey: acqKeys.torrents });
    qc.invalidateQueries({ queryKey: libraryKeys.roots });
  };
  const update = useMutation({
    mutationFn: ({ id, ...body }: { id: string; enabled?: boolean; priority?: number }) => libraryApi.setTorrentSource(id, body),
    onSuccess: refresh,
    onError: (e) => toast.error(errorText(e)),
  });
  const addMagnet = useMutation({ mutationFn: libraryApi.addMagnet, onSuccess: refresh, onError: (e) => toast.error(errorText(e)) });
  return { ...query, update, addMagnet };
}
