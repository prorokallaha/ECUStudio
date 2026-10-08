"use client";
import { useCallback, useState, type DragEvent } from "react";

/** Drag-and-drop of .bin files anywhere on a surface. */
export function useFileDrop(onFiles: (files: File[]) => void) {
  const [over, setOver] = useState(false);
  const onDragOver = useCallback((e: DragEvent) => { if (e.dataTransfer.types.includes("Files")) { e.preventDefault(); setOver(true); } }, []);
  const onDragLeave = useCallback((e: DragEvent) => { if (e.currentTarget === e.target) setOver(false); }, []);
  const onDrop = useCallback((e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    const files = Array.from(e.dataTransfer.files);
    if (files.length) onFiles(files);
  }, [onFiles]);
  return { over, bind: { onDragOver, onDragLeave, onDrop } };
}
