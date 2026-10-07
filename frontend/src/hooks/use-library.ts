"use client";
import { useEffect, useRef } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { libraryApi } from "@/services/api-library";
import { ApiError } from "@/services/api";
import type { LibraryEntryQuery } from "@/types/library";
import { keys } from "./use-analysis";

export const libraryKeys = {
  roots: ["library", "roots"] as const,
  entries: (q: LibraryEntryQuery) => ["library", "entries", q] as const,
  entriesAll: ["library", "entries"] as const,
  matches: (analysisId: string) => ["library", "matches", analysisId] as const,
};

export const errorText = (e: unknown) => (e instanceof ApiError ? `${e.code}: ${e.message}` : String(e));

/** Library roots; polls while any root is being scanned and refreshes the entry list when a scan finishes. */
export function useLibraryRoots() {
  const qc = useQueryClient();
  const query = useQuery({
    queryKey: libraryKeys.roots,
    queryFn: libraryApi.roots,
    refetchInterval: (q) => (q.state.data?.some((s) => s.state === "Scanning") ? 1500 : false),
  });
  const scanning = query.data?.filter((s) => s.state === "Scanning").length ?? 0;
  const prev = useRef(scanning);
  useEffect(() => {
    if (prev.current > scanning) qc.invalidateQueries({ queryKey: libraryKeys.entriesAll });
    prev.current = scanning;
  }, [scanning, qc]);
  return query;
}

export function useLibraryEntries(q: LibraryEntryQuery) {
  return useQuery({ queryKey: libraryKeys.entries(q), queryFn: () => libraryApi.entries(q), placeholderData: keepPreviousData });
}

/** Library entries that match the analysed binary, best first. */
export function useDefinitionMatches(analysisId?: string | null) {
  return useQuery({ queryKey: libraryKeys.matches(analysisId ?? ""), queryFn: () => libraryApi.matches(analysisId!), enabled: !!analysisId, staleTime: 30_000 });
}

/** Mutations on library roots; every change refreshes roots and entries. */
export function useLibraryMutations() {
  const qc = useQueryClient();
  const refresh = () => {
    qc.invalidateQueries({ queryKey: libraryKeys.roots });
    qc.invalidateQueries({ queryKey: libraryKeys.entriesAll });
    qc.invalidateQueries({ queryKey: ["library", "matches"] });
  };
  const onError = (e: unknown) => toast.error(errorText(e));
  // A new root is indexed right away; the scan runs in the background and the roots list polls its state.
  const scanNew = (r: { id: string }) => libraryApi.scan(r.id).catch(onError).finally(refresh);
  return {
    addRoot: useMutation({ mutationFn: libraryApi.addRoot, onSuccess: scanNew, onError }),
    addTorrent: useMutation({ mutationFn: ({ file, downloadPath }: { file: File; downloadPath?: string | null }) => libraryApi.addTorrent(file, downloadPath), onSuccess: scanNew, onError }),
    updateRoot: useMutation({ mutationFn: ({ rootId, ...body }: { rootId: string; name?: string | null; downloadPath?: string | null }) => libraryApi.updateRoot(rootId, body), onSuccess: refresh, onError }),
    removeRoot: useMutation({ mutationFn: libraryApi.removeRoot, onSuccess: refresh, onError }),
    scan: useMutation({ mutationFn: libraryApi.scan, onSuccess: () => qc.invalidateQueries({ queryKey: libraryKeys.roots }), onError }),
  };
}

/** Removes the project's bound definition. */
export function useUnbindDefinition(projectId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => libraryApi.definition.unbind(projectId),
    onSuccess: (p) => qc.setQueryData(keys.project(projectId), p),
    onError: (e) => toast.error(errorText(e)),
  });
}
