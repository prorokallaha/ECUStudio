"use client";
import { AlertTriangle } from "lucide-react";
import type { CompatibilityReport, CompatibilityStatus, LibraryEntry, LibraryIdentifiers, MatchLevel } from "@/types/library";
import type { Tone } from "@/lib/colors";
import { Badge, Tooltip } from "@/components/ui";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

export const levelTone: Record<MatchLevel, Tone> = { Exact: "ok", Strong: "calc", Probable: "attn", Weak: "unknown", Unknown: "unknown" };
export const compatTone: Record<CompatibilityStatus, Tone> = { Compatible: "ok", Warning: "warn", Incompatible: "danger" };

/** Only Exact and Strong count as "the definition for this ECU"; anything else is a hint that needs checking. */
export const isUncertain = (level?: MatchLevel | null) => level !== "Exact" && level !== "Strong";

export function MatchLevelBadge({ level }: { level?: MatchLevel | null }) {
  const t = useT();
  const l = level ?? "Unknown";
  return (
    <span className="inline-flex items-center gap-1">
      <Badge tone={levelTone[l]} className="normal-case tracking-normal">{t.tx(`library.level.${l}`, l)}</Badge>
      {isUncertain(l) && (
        <Tooltip content={t("library.uncertainTip")}>
          <Badge tone="warn" className="normal-case tracking-normal"><AlertTriangle className="size-2.5" />{t("library.uncertain")}</Badge>
        </Tooltip>
      )}
    </span>
  );
}

export function CompatibilityBadge({ status }: { status: CompatibilityStatus }) {
  const t = useT();
  return <Badge tone={compatTone[status]}>{t.tx(`library.compat.${status}`, status)}</Badge>;
}

export function AvailabilityBadge({ entry }: { entry: Pick<LibraryEntry, "available"> }) {
  const t = useT();
  return entry.available
    ? <Badge tone="ok">{t("library.available")}</Badge>
    : <Tooltip content={t("library.notDownloadedTip")}><Badge tone="unknown">{t("library.notDownloaded")}</Badge></Tooltip>;
}

export function FormatBadge({ format }: { format: string }) {
  const t = useT();
  return <Badge tone="calc">{t.tx(`library.format.${format}`, format)}</Badge>;
}

/** SW / HW / OEM / family identifiers, compact. */
export function Identifiers({ ids, className }: { ids: LibraryIdentifiers; className?: string }) {
  const t = useT();
  const rows: [string, string[]][] = [
    [t("library.idSw"), ids.softwareNumbers], [t("library.idHw"), ids.hardwareNumbers], [t("library.idOem"), ids.oemNumbers], [t("library.idFamily"), ids.ecuFamilies],
  ];
  const shown = rows.filter(([, v]) => v.length);
  if (!shown.length) return <span className={cn("text-fg-subtle", className)}>{t("library.noIds")}</span>;
  return (
    <div className={cn("space-y-0.5", className)}>
      {shown.map(([k, v]) => (
        <div key={k} className="flex gap-1.5"><span className="w-10 shrink-0 text-fg-subtle">{k}</span><span className="num break-all">{v.join(", ")}</span></div>
      ))}
    </div>
  );
}

/** Compatibility status, counters and reasons. */
export function CompatibilityDetails({ c }: { c: CompatibilityReport }) {
  const t = useT();
  return (
    <div className="space-y-1.5 text-xs">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <CompatibilityBadge status={c.status} />
        <span className="text-fg-muted">{t("library.mapsDecoded")}: <span className="num text-fg">{c.mapsDecoded}/{c.mapCount}</span></span>
        <span className="text-fg-muted">{t("library.mapsOutOfRange")}: <span className={cn("num", c.mapsOutOfRange ? "text-danger" : "text-fg")}>{c.mapsOutOfRange}</span></span>
        <span className="text-fg-muted">{t("library.axesNotMonotonic")}: <span className={cn("num", c.axesNotMonotonic ? "text-warn" : "text-fg")}>{c.axesNotMonotonic}</span></span>
        <span className="text-fg-muted">{t("library.softwareMatches")}: <span className="text-fg">{c.softwareMatches == null ? t("common.unknown") : c.softwareMatches ? t("common.yes") : t("common.no")}</span></span>
      </div>
      {c.reasons.length > 0 && (
        <ul className="list-disc space-y-0.5 pl-4 text-fg-muted">{c.reasons.map((r, i) => <li key={i}>{r}</li>)}</ul>
      )}
    </div>
  );
}
