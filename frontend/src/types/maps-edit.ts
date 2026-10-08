import type { components } from "./api.generated";

/** Map-editing types (aliases over the OpenAPI schema) plus UI-only types of the maps workspace. */
type S = components["schemas"];
export type Cell = S["Cell"];
export type MapOperation = S["MapOperation"];
export type MapOperationKind = S["MapOperationKind"];
export type MapEditRequest = S["MapEditRequest"];
export type MapEditPreview = S["MapEditPreview"];
export type CellChange = S["CellChange"];
export type EditState = S["EditState"];
export type ChangedRegion = S["ChangedRegion"];
export type WorkingHexDto = S["WorkingHexDto"];

/** Tabs of the map editor. */
export type MapEditorView = "table" | "2d" | "3d" | "heatmap" | "hex" | "compare";

/** What the 3D surface shows. */
export type SurfaceOverlay = "mod" | "stock" | "both" | "delta";

/** Filter groups of the map tree. */
export type MapTreeFilter = "all" | "defined" | "modified" | "candidates" | "unknown" | "user";

/**
 * Values of one map as currently held by the working buffer of the edited file (after undoable patches),
 * decoded locally from the working bytes with the map's scaling. `null` values mean the bytes could not be decoded.
 */
export interface WorkingMapValues {
  /** Row-major values, same layout as MapData.values. */
  values: number[];
  /** Bytes per cell as uppercase hex (row-major), from the working buffer. */
  bytes: string[];
  /** Absolute file address of each cell (row-major). */
  addresses: number[];
  /** Cells whose working bytes differ from the analysed file. */
  editedCells: number;
  /** Storage order detected by matching the analysed values. */
  order: "row" | "col";
}
