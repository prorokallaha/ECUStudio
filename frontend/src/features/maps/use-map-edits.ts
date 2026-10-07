"use client";
import { useCallback, useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import type { AnalysisReport, DataType, Endianness, MapData } from "@/types/domain";
import type { EditState, MapEditPreview, MapOperation, WorkingMapValues } from "@/types/maps-edit";
import { mapsApi } from "@/services/api-maps";
import { ApiError, base64ToBytes } from "@/services/api";
import { keys, useProject } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";

export const editKeys = {
  state: (p: string, f: string) => ["edits", p, f] as const,
  working: (f: string) => ["working-map", f] as const,
  workingMap: (f: string, mapId: string) => ["working-map", f, mapId] as const,
};

export interface EditTarget { projectId: string; fileId: string; fileName: string }

/** The project file that was analysed as "modified": map edits are patches on its working buffer. */
export function useEditTarget(r: AnalysisReport): EditTarget | null {
  const { projectId } = useWorkspace();
  const project = useProject(projectId);
  return useMemo(() => {
    const f = project.data?.files.find((x) => x.sha256 === r.modifiedSha256);
    return projectId && f ? { projectId, fileId: f.id, fileName: f.name } : null;
  }, [project.data, projectId, r.modifiedSha256]);
}

export function useEditState(target: EditTarget | null) {
  return useQuery({
    queryKey: editKeys.state(target?.projectId ?? "", target?.fileId ?? ""),
    queryFn: () => mapsApi.state(target!.projectId, target!.fileId),
    enabled: !!target,
  });
}

/** Map ids touched by patches of the working buffer (EditState.regions). */
export function editedMapIds(state?: EditState | null): Set<string> {
  return new Set((state?.regions ?? []).map((g) => g.mapId).filter((x): x is string => !!x));
}

export const typeSize = (t: DataType) => (t.endsWith("32") ? 4 : t.endsWith("16") ? 2 : 1);

function readRaw(view: DataView, at: number, t: DataType, endian: Endianness): number {
  const le = endian === "Little";
  switch (t) {
    case "UInt8": return view.getUint8(at);
    case "Int8": return view.getInt8(at);
    case "UInt16": return view.getUint16(at, le);
    case "Int16": return view.getInt16(at, le);
    case "UInt32": return view.getUint32(at, le);
    case "Int32": return view.getInt32(at, le);
    case "Float32": return view.getFloat32(at, le);
  }
}

const tidy = (v: number) => Math.round(v * 1e6) / 1e6;

/**
 * Decodes the map from working-buffer bytes. The storage order (row/column major) is not in MapSummary, so it is
 * detected by decoding the ORIGINAL bytes both ways and matching the analysed values; if neither matches the
 * layout is unknown and null is returned (the editor then falls back to the analysed values).
 */
export function decodeWorking(data: MapData, working: Uint8Array, original: Uint8Array): WorkingMapValues | null {
  const s = data.summary;
  const rows = data.yAxis.length || 1, cols = data.xAxis.length || 1, size = typeSize(s.dataType);
  if (working.length < rows * cols * size || original.length < rows * cols * size) return null;
  const wv = new DataView(working.buffer, working.byteOffset, working.byteLength);
  const ov = new DataView(original.buffer, original.byteOffset, original.byteLength);
  const tol = Math.abs(s.factor || 1) / 2 + 1e-6;
  const pos = (order: "row" | "col", r: number, c: number) => (order === "row" ? r * cols + c : c * rows + r) * size;
  const matches = (order: "row" | "col") => {
    for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) {
      const v = readRaw(ov, pos(order, r, c), s.dataType, s.endian) * s.factor + s.offset;
      if (Math.abs(v - data.values[r * cols + c]) > tol) return false;
    }
    return true;
  };
  const order = matches("row") ? "row" : matches("col") ? "col" : null;
  if (!order) return null;
  const values: number[] = [], bytes: string[] = [], addresses: number[] = [];
  let editedCells = 0;
  for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) {
    const p = pos(order, r, c);
    values.push(tidy(readRaw(wv, p, s.dataType, s.endian) * s.factor + s.offset));
    let hexText = "", changed = false;
    for (let k = 0; k < size; k++) {
      hexText += working[p + k].toString(16).toUpperCase().padStart(2, "0");
      if (working[p + k] !== original[p + k]) changed = true;
    }
    bytes.push(hexText);
    addresses.push(s.address + p);
    if (changed) editedCells++;
  }
  return { values, bytes, addresses, editedCells, order };
}

/** Current (working-buffer) values of a map; refetched after every apply / undo / redo / revert. */
export function useWorkingMap(target: EditTarget | null, data: MapData) {
  const s = data.summary;
  return useQuery({
    queryKey: [...editKeys.workingMap(target?.fileId ?? "", data.id), s.address],
    enabled: !!target,
    staleTime: 30_000,
    queryFn: async () => {
      const length = (data.yAxis.length || 1) * (data.xAxis.length || 1) * typeSize(s.dataType);
      const dto = await mapsApi.workingHex(target!.projectId, target!.fileId, s.address, length);
      return decodeWorking(data, base64ToBytes(dto.working), base64ToBytes(dto.original));
    },
  });
}

const errText = (e: unknown) => (e instanceof ApiError ? `${e.code}: ${e.message}` : String(e));

/** Preview / apply / revert / undo / redo for one map; every mutation invalidates working data and edit state. */
export function useMapEditActions(target: EditTarget | null, mapId: string) {
  const qc = useQueryClient();
  const refresh = useCallback((state?: EditState) => {
    if (!target) return;
    if (state) qc.setQueryData(editKeys.state(target.projectId, target.fileId), state);
    else qc.invalidateQueries({ queryKey: editKeys.state(target.projectId, target.fileId) });
    qc.invalidateQueries({ queryKey: editKeys.working(target.fileId) });
    qc.invalidateQueries({ queryKey: keys.project(target.projectId) });
  }, [qc, target]);

  const run = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | null> => {
    try { return await fn(); } catch (e) { toast.error(errText(e)); return null; }
  }, []);

  return useMemo(() => ({
    preview: (op: MapOperation) => (target ? run<MapEditPreview>(() => mapsApi.preview(target.projectId, target.fileId, mapId, op)) : Promise.resolve(null)),
    apply: async (op: MapOperation, description?: string) => {
      if (!target) return null;
      const st = await run(() => mapsApi.apply(target.projectId, target.fileId, mapId, op, description));
      if (st) refresh(st);
      return st;
    },
    revert: async () => { if (!target) return null; const st = await run(() => mapsApi.revert(target.projectId, target.fileId, mapId)); if (st) refresh(st); return st; },
    undo: async () => { if (!target) return null; const st = await run(() => mapsApi.undo(target.projectId, target.fileId)); if (st) refresh(st); return st; },
    redo: async () => { if (!target) return null; const st = await run(() => mapsApi.redo(target.projectId, target.fileId)); if (st) refresh(st); return st; },
  }), [target, mapId, run, refresh]);
}
