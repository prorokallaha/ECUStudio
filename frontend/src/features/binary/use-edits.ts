"use client";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import type { EditState, HexEdit, RevertRequest } from "@/types/editing";
import { editingApi } from "@/services/api-editing";
import { ApiError } from "@/services/api";

export const editKeys = {
  state: (projectId: string, fileId: string) => ["edits", projectId, fileId] as const,
  content: (projectId: string, fileId: string) => ["file-content", projectId, fileId] as const,
};

/** Unsaved edit state of a project file (backend patch model). */
export function useEditState(projectId: string | null, fileId: string | null) {
  return useQuery({ queryKey: editKeys.state(projectId ?? "", fileId ?? ""), queryFn: () => editingApi.state(projectId!, fileId!), enabled: !!projectId && !!fileId });
}

/** Raw stored bytes of a project file. Stored files are immutable (saving creates a new file), so cached forever. */
export function useFileContent(projectId: string | null, fileId: string | null) {
  return useQuery({
    queryKey: editKeys.content(projectId ?? "", fileId ?? ""),
    queryFn: ({ signal }) => editingApi.content(projectId!, fileId!, signal),
    enabled: !!projectId && !!fileId,
    staleTime: Infinity,
    gcTime: 5 * 60_000,
  });
}

const errText = (e: unknown) => (e instanceof ApiError ? `${e.code}: ${e.message}` : String(e));

/** Edit operations. Every one returns the new EditState from the backend, which replaces the cached one. */
export function useEditActions(projectId: string | null, fileId: string | null) {
  const qc = useQueryClient();
  const key = editKeys.state(projectId ?? "", fileId ?? "");
  const onSuccess = (s: EditState) => qc.setQueryData(key, s);
  const onError = (e: unknown) => toast.error(errText(e));
  const ready = () => { if (!projectId || !fileId) throw new Error("no file"); return [projectId, fileId] as const; };
  const write = useMutation({ mutationFn: (body: HexEdit) => editingApi.writeHex(...ready(), body), onSuccess, onError });
  const undo = useMutation({ mutationFn: () => editingApi.undo(...ready()), onSuccess, onError });
  const redo = useMutation({ mutationFn: () => editingApi.redo(...ready()), onSuccess, onError });
  const revert = useMutation({ mutationFn: (body: Partial<RevertRequest>) => editingApi.revert(...ready(), body), onSuccess, onError });
  const busy = write.isPending || undo.isPending || redo.isPending || revert.isPending;
  return { write, undo, redo, revert, busy };
}
