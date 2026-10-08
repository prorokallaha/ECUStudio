"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import type { HexRegion } from "@/types/domain";
import { api, base64ToBytes } from "@/services/api";

export const PAGE = 4096;

export interface HexPage { offset: number; mod: Uint8Array; stock: Uint8Array | null; regions: HexRegion[] }

/** Range-based loader: fetches 4 KB pages on demand (only what is visible), keeps an LRU of pages. */
export function useHexPages(analysisId: string, fileSize: number) {
  const cache = useRef(new Map<number, HexPage>());
  const inflight = useRef(new Set<number>());
  const [, force] = useState(0);

  useEffect(() => { cache.current.clear(); inflight.current.clear(); force((x) => x + 1); }, [analysisId]);

  const ensure = useCallback((pageIndexes: number[]) => {
    for (const p of pageIndexes) {
      const off = p * PAGE;
      if (off >= fileSize || cache.current.has(p) || inflight.current.has(p)) continue;
      inflight.current.add(p);
      api.analyses.hex(analysisId, off, PAGE).then((d) => {
        cache.current.set(p, { offset: d.offset, mod: base64ToBytes(d.modified), stock: d.stock ? base64ToBytes(d.stock) : null, regions: d.regions });
        if (cache.current.size > 64) cache.current.delete(cache.current.keys().next().value!);
        force((x) => x + 1);
      }).finally(() => inflight.current.delete(p));
    }
  }, [analysisId, fileSize]);

  const byteAt = useCallback((offset: number): { mod: number; stock: number | null } | null => {
    const page = cache.current.get(Math.floor(offset / PAGE));
    if (!page) return null;
    const i = offset - page.offset;
    if (i < 0 || i >= page.mod.length) return null;
    return { mod: page.mod[i], stock: page.stock ? page.stock[i] : null };
  }, []);

  const regionsAt = useCallback((offset: number): HexRegion[] => {
    const page = cache.current.get(Math.floor(offset / PAGE));
    return page ? page.regions.filter((r) => offset >= r.start && offset < r.end) : [];
  }, []);

  return { ensure, byteAt, regionsAt };
}

export function readWord(bytes: (number | null)[], size: 1 | 2 | 4, endian: "Big" | "Little", signed: boolean, float = false): number | null {
  if (bytes.some((b) => b === null) || bytes.length < size) return null;
  const b = (endian === "Big" ? bytes.slice(0, size) : bytes.slice(0, size).reverse()) as number[];
  const buf = new DataView(new Uint8Array(b).buffer);
  if (float && size === 4) return buf.getFloat32(0, false);
  if (size === 1) return signed ? buf.getInt8(0) : buf.getUint8(0);
  if (size === 2) return signed ? buf.getInt16(0, false) : buf.getUint16(0, false);
  return signed ? buf.getInt32(0, false) : buf.getUint32(0, false);
}
