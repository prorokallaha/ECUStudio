"use client";
import { useState } from "react";
import { FileUp } from "lucide-react";
import type { AnalysisReport } from "@/types/domain";
import { Badge, Button, Card, CardBody, CardHeader, SectionTitle } from "@/components/ui";
import { CompatibilityDetails, FormatBadge, MatchLevelBadge } from "./badges";
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
