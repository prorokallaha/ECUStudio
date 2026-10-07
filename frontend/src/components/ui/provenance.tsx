"use client";
import type { Severity, SourceType } from "@/types/domain";
import { Badge } from "./badge";
import { Tooltip } from "./tooltip";
import { confidenceBadge, SOURCE_META, severityLabel } from "@/lib/format";
import { severityTone, type Tone } from "@/lib/colors";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";
import { Bot, Calculator, Database, FileCode2, ScanSearch, ShieldCheck, User, HelpCircle, FileText } from "lucide-react";

const familyTone: Record<string, Tone> = { oem: "ok", damos: "ok", a2l: "ok", db: "calc", calc: "calc", ai: "ai", user: "attn", scan: "unknown", log: "ok", unknown: "unknown" };
const familyIcon = { oem: ShieldCheck, damos: FileCode2, a2l: FileCode2, db: Database, calc: Calculator, ai: Bot, user: User, scan: ScanSearch, log: FileText, unknown: HelpCircle } as const;

/** One badge system for provenance across the whole app (spec: OEM, DAMOS, A2L, Database, Calculated, AI inferred, User entered). */
export function SourceBadge({ source, className }: { source?: SourceType | null; className?: string }) {
  const t = useT();
  const meta = SOURCE_META[source ?? "Unknown"];
  const Icon = familyIcon[meta.family];
  return (
    <Badge tone={familyTone[meta.family]} className={cn("normal-case tracking-normal font-medium", className)}>
      <Icon className="size-3" />{t.tx(`source.${source ?? "Unknown"}`, meta.label)}
    </Badge>
  );
}

const confTone = { HIGH: "ok", MEDIUM: "attn", LOW: "warn", UNKNOWN: "unknown" } as const;

export function ConfidenceBadge({ score, className, showScore = true }: { score?: number | null; className?: string; showScore?: boolean }) {
  const t = useT();
  const level = confidenceBadge(score);
  return (
    <Tooltip content={t("confidence.tooltip", { score: score !== undefined && score !== null ? score.toFixed(2) : "?" })}>
      <Badge tone={confTone[level]} className={className}>
        {t.tx(`confidence.${level}`, level)}{showScore && score ? <span className="num font-normal opacity-80">{score.toFixed(2)}</span> : null}
      </Badge>
    </Tooltip>
  );
}

export function SeverityBadge({ severity, className }: { severity?: Severity | null; className?: string }) {
  const t = useT();
  const s = severity ?? "Unknown";
  return <Badge tone={severityTone[s]} className={className}>{t.tx(`severity.${s}`, severityLabel[s])}</Badge>;
}

export function AIBadge({ className }: { className?: string }) {
  return <Badge tone="ai" className={className}><Bot className="size-3" />AI</Badge>;
}
