import type { components } from "./api.generated";

/** Aliases over the generated editing schema (patch model on the backend; the frontend never mutates bytes itself). */
type S = components["schemas"];
export type EditState = S["EditState"];
export type EditSummary = S["EditSummary"];
export type ChangedRegion = S["ChangedRegion"];
export type WorkingHexDto = S["WorkingHexDto"];
export type HexEdit = S["HexEdit"];
export type RevertRequest = S["RevertRequest"];
export type SaveRequest = S["SaveRequest"];
export type SaveCheck = S["SaveCheck"];
export type SaveCheckItem = S["SaveCheckItem"];
export type SaveCheckStatus = S["SaveCheckStatus"];
export type SaveResult = S["SaveResult"];
export type ByteRange = S["ByteRange"];
export type ChecksumReport = S["ChecksumReport"];
export type ChecksumStatus = S["ChecksumStatus"];

/** Display-only interpretation of bytes. Never sent to the server and never affects stored data. */
export type WordKind = "8" | "16" | "32" | "f32";
export type NumBase = "hex" | "dec";
export type ByteOrder = "Big" | "Little";

/** One visual row of a hex view: a 16-byte data row, a folded padding run, or the header of an expanded run. */
export type DisplayRow =
  | { kind: "data"; offset: number }
  | { kind: "fold"; start: number; end: number; value: number }
  | { kind: "unfold"; start: number; end: number; value: number };

/** A contiguous run of row-aligned identical 0x00/0xFF bytes; `end` is exclusive. Offsets are original file offsets. */
export interface PaddingRun { start: number; end: number; value: number }

/** A contiguous run of differing bytes between two buffers; `end` is exclusive. */
export interface DiffRange { start: number; end: number }
