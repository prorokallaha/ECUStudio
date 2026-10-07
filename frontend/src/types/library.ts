import type { components } from "./api.generated";

/** Definition library and project definition binding (aliases over the OpenAPI-generated schema). */
type S = components["schemas"];
export type LibraryRoot = S["LibraryRoot"];
export type LibraryRootKind = S["LibraryRootKind"];
export type LibraryRootStatus = S["LibraryRootStatus"];
export type ScanState = S["ScanState"];
export type LibraryEntry = S["LibraryEntry"];
export type LibraryFormat = S["LibraryFormat"];
export type LibraryIdentifiers = S["LibraryIdentifiers"];
export type LibrarySearchResult = S["LibrarySearchResult"];
export type DefinitionMatch = S["DefinitionMatch"];
export type MatchLevel = NonNullable<S["MatchLevel"]>;
export type ProjectDefinition = S["ProjectDefinition"];
export type DefinitionPreview = S["DefinitionPreview"];
export type CompatibilityReport = S["CompatibilityReport"];
export type CompatibilityStatus = S["CompatibilityStatus"];
export type DefinitionBinding = S["DefinitionBinding"];
export type DefinitionOrigin = S["DefinitionOrigin"];
export type DefinitionAcquisition = S["DefinitionAcquisition"];
export type AcquisitionState = S["AcquisitionState"];
export type AcquisitionCandidate = S["AcquisitionCandidate"];
export type AcquisitionSettings = S["AcquisitionSettings"];
export type DefinitionCompatibilityResult = S["DefinitionCompatibilityResult"];
export type DefinitionFit = S["DefinitionFit"];
export type MatchConfidence = NonNullable<S["MatchConfidence"]>;
export type TransferInfo = S["TransferInfo"];
export type TorrentSource = S["TorrentSource"];
export type TorrentSourcesDto = S["TorrentSourcesDto"];

export const LIBRARY_FORMATS: LibraryFormat[] = ["A2L", "Damos", "Xdf", "EcuDef", "Ols", "Kp", "Binary", "Hex", "Csv", "Xml", "Archive", "Other"];

export interface LibraryEntryQuery {
  q?: string;
  format?: LibraryFormat | "";
  definitionsOnly?: boolean;
  offset?: number;
  limit?: number;
}

/** A definition source for the import dialog: a local file or a library entry. */
export type DefinitionSource = { kind: "file"; file: File } | { kind: "library"; entry: LibraryEntry };
