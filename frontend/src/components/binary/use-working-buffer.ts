"use client";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { EditState } from "@/types/editing";
import { editingApi } from "@/services/api-editing";
import { base64ToBytes } from "@/services/api";
import { byteRangesToRanges, rangeIndex } from "./row-model";

export const WPAGE = 16 * 1024;

interface WorkingPage { offset: number; bytes: Uint8Array; sha: string }

/**
 * Read-only view of a file's working buffer (stored original + unsaved patches held by the backend).
 * Outside the changed ranges the working buffer equals the original, so only pages that contain changes are
 * fetched (lazily, when visible) from /edits/hex. Pages from a previous working state stay visible until refreshed.
 */
export function useWorkingBuffer(projectId: string | null, fileId: string | null, original: Uint8Array | undefined, state: EditState | undefined) {
  const pages = useRef(new Map<number, WorkingPage>());
  const inflight = useRef(new Map<number, string>());
  const [, force] = useState(0);
  const sha = state?.workingSha256 ?? "";
  const size = state?.size ?? original?.length ?? 0;

  useEffect(() => { pages.current.clear(); inflight.current.clear(); force((x) => x + 1); }, [projectId, fileId]);

  const changed = useMemo(() => rangeIndex(byteRangesToRanges(state?.changedRanges ?? [])), [state?.changedRanges]);

  const pageHasChanges = useCallback((p: number) => {
    const a = p * WPAGE, z = a + WPAGE;
    const r = changed.find(a) ?? changed.next(a, false);
    return !!r && r.start < z;
  }, [changed]);

  /** Fetches the working bytes of the visible pages that contain unsaved changes. */
  const ensure = useCallback((from: number, to: number) => {
    if (!projectId || !fileId || !sha) return;
    for (let p = Math.floor(from / WPAGE); p <= Math.floor(Math.max(from, to - 1) / WPAGE); p++) {
      const off = p * WPAGE;
      if (off >= size || !pageHasChanges(p)) continue;
      if (pages.current.get(p)?.sha === sha || inflight.current.get(p) === sha) continue;
      inflight.current.set(p, sha);
      editingApi.hex(projectId, fileId, off, Math.min(WPAGE, size - off)).then((d) => {
        pages.current.set(p, { offset: d.offset, bytes: base64ToBytes(d.working), sha });
        if (pages.current.size > 96) pages.current.delete(pages.current.keys().next().value!);
        force((x) => x + 1);
      }).catch(() => {}).finally(() => { if (inflight.current.get(p) === sha) inflight.current.delete(p); });
    }
  }, [projectId, fileId, sha, size, pageHasChanges]);

  /** Working byte at offset; null while the page with unsaved changes is still loading. */
  const working = useCallback((offset: number): number | null => {
    if (!original || offset < 0 || offset >= size) return null;
    if (!changed.has(offset)) return original[offset];
    const page = pages.current.get(Math.floor(offset / WPAGE));
    if (!page) return null;
    const i = offset - page.offset;
    return i >= 0 && i < page.bytes.length ? page.bytes[i] : null;
  }, [original, size, changed]);

  const orig = useCallback((offset: number): number | null => (original && offset >= 0 && offset < original.length ? original[offset] : null), [original]);

  return { ensure, working, original: orig, isChanged: changed.has, changed, size };
}
