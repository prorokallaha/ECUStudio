"use client";
import { useState } from "react";
import Link from "next/link";
import { AlertTriangle, FileUp, Link2 } from "lucide-react";
import type { AnalysisReport } from "@/types/domain";
import type { DefinitionMatch, LibraryEntry } from "@/types/library";
import { useDefinitionMatches, errorText } from "@/hooks/use-library";
import { useWorkspace } from "@/hooks/use-workspace";
import { Badge, Button, Card, CardBody, CardHeader, SectionTitle, Skeleton } from "@/components/ui";
import { AvailabilityBadge, CompatibilityDetails, FormatBadge, Identifiers, isUncertain, MatchLevelBadge } from "./badges";
import { DefinitionImportDialog } from "./definition-import-dialog";
import { useT } from "@/i18n";

/** Which definition this analysis actually used (report.definitionBinding), plus the analysis' definition notes. */
export function DefinitionBindingCard({ r, projectId, className }: { r: AnalysisReport; projectId: string; className?: string }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const b = r.definitionBinding;
  return (
    <Card className={className}>
      <CardHeader
        title={t("library.usedTitle")}
        actions={<Button size="xs" onClick={() => setOpen(true)}><FileUp className="size-3" />{t("library.importButton")}</Button>}
      />
      <CardBody className="space-y-2 text-xs">
        {b ? (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium">{b.name}</span>
              <FormatBadge format={b.format} />
              <Badge tone="unknown">{t.tx(`library.origin.${b.origin}`, b.origin)}</Badge>
              {b.level && <MatchLevelBadge level={b.level} />}
              <Badge tone={b.applied ? "ok" : "warn"}>{b.applied ? t("library.applied") : t("library.notApplied")}</Badge>
            </div>
            <div className="text-fg-muted">{b.applied ? t("library.appliedHint") : t("library.notAppliedHint")}</div>
            {b.reasons.length > 0 && (
              <div>
                <SectionTitle className="mb-0.5">{t("library.whyMatched")}</SectionTitle>
                <ul className="space-y-0.5 text-fg-muted">{b.reasons.map((x, i) => <li key={i}>✓ {x}</li>)}</ul>
              </div>
            )}
            {b.compatibility && (
              <div>
                <SectionTitle className="mb-0.5">{t("library.compatibility")}</SectionTitle>
                <CompatibilityDetails c={b.compatibility} />
              </div>
            )}
          </>
        ) : <div className="text-fg-muted">{t("library.noBinding")}</div>}
        <div className="text-fg-muted">{t("library.usedSource", { v: r.definitionSource })}</div>
        {r.definitionNotes.length > 0 && (
          <div>
            <SectionTitle className="mb-0.5">{t("library.notes")}</SectionTitle>
            <ul className="list-disc space-y-0.5 pl-4 text-fg-muted">{r.definitionNotes.map((n, i) => <li key={i}>{n}</li>)}</ul>
          </div>
        )}
      </CardBody>
      <DefinitionImportDialog open={open} onClose={() => setOpen(false)} projectId={projectId} />
    </Card>
  );
}

/** Library entries that match the analysed binary. Exact/Strong first; Probable/Weak are listed separately as uncertain. */
export function DefinitionMatchesCard({ analysisId, projectId, className }: { analysisId: string; projectId: string; className?: string }) {
  const t = useT();
  const { href } = useWorkspace();
  const matches = useDefinitionMatches(analysisId);
  const [entry, setEntry] = useState<LibraryEntry | null>(null);
  const list = matches.data ?? [];
  const confident = list.filter((m) => !isUncertain(m.level));
  const uncertain = list.filter((m) => isUncertain(m.level));
  return (
    <Card className={className}>
      <CardHeader title={t("library.matchesTitle")} subtitle={t("library.matchesSubtitle")} actions={<Link href={href("library")} className="text-2xs text-calc hover:underline">{t("library.openLibrary")}</Link>} />
      <CardBody className="space-y-3 text-xs">
        {matches.isLoading && <Skeleton className="h-16" />}
        {matches.error && <div className="text-danger">{errorText(matches.error)}</div>}
        {matches.data && !list.length && <div className="text-fg-muted">{t("library.noMatches")}</div>}
        {confident.length > 0 && (
          <div className="space-y-1.5">
            <SectionTitle>{t("library.confident")}</SectionTitle>
            {confident.map((m) => <MatchRow key={m.entry.id} m={m} onBind={setEntry} />)}
          </div>
        )}
        {uncertain.length > 0 && (
          <div className="space-y-1.5">
            <SectionTitle className="flex items-center gap-1 text-warn"><AlertTriangle className="size-3" />{t("library.uncertainGroup")}</SectionTitle>
            {uncertain.map((m) => <MatchRow key={m.entry.id} m={m} onBind={setEntry} />)}
          </div>
        )}
      </CardBody>
      <DefinitionImportDialog open={!!entry} onClose={() => setEntry(null)} projectId={projectId} entry={entry} />
    </Card>
  );
}

function MatchRow({ m, onBind }: { m: DefinitionMatch; onBind: (e: LibraryEntry) => void }) {
  const t = useT();
  const e = m.entry;
  const uncertain = isUncertain(m.level);
  return (
    <div className={uncertain ? "rounded border border-dashed border-border px-2.5 py-2 opacity-90" : "rounded border border-border px-2.5 py-2"}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="min-w-0 truncate font-medium" title={e.relativePath}>{e.title ?? e.relativePath.split(/[\\/]/).pop()}</span>
        <MatchLevelBadge level={m.level} />
        <span className="num text-fg-subtle">{t("library.score", { s: m.score.toFixed(2) })}</span>
        <FormatBadge format={e.format} />
        <AvailabilityBadge entry={e} />
        {!m.isDefinition && <Badge tone="unknown" className="normal-case tracking-normal">{t("library.notDefinition")}</Badge>}
        {m.isDefinition && !m.importable && <Badge tone="warn" className="normal-case tracking-normal">{t("library.notImportableFormat")}</Badge>}
        {m.importable && (
          <Button size="xs" variant={uncertain ? "secondary" : "primary"} className="ml-auto" disabled={!e.available} title={e.available ? undefined : t("library.notDownloadedTip")} onClick={() => onBind(e)}>
            <Link2 className="size-3" />{t("library.bind")}
          </Button>
        )}
      </div>
      <div className="num mt-0.5 truncate text-[10px] text-fg-subtle">{e.relativePath}</div>
      {m.reasons.length > 0 && <ul className="mt-1 space-y-0.5 text-fg-muted">{m.reasons.map((x, i) => <li key={i}>✓ {x}</li>)}</ul>}
      <Identifiers ids={e.identifiers} className="mt-1 text-[11px]" />
    </div>
  );
}
