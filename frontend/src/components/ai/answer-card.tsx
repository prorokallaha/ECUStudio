"use client";
import type { AssistantAnswer, Evidence } from "@/types/domain";
import { AIBadge, ConfidenceBadge, SectionTitle } from "@/components/ui";
import { useT } from "@/i18n";

export function EvidenceList({ evidence }: { evidence: Evidence[] }) {
  if (!evidence.length) return null;
  return (
    <ul className="space-y-0.5">
      {evidence.map((e, i) => (
        <li key={i} className="flex gap-1.5 text-[11px]">
          <span className="num shrink-0 text-calc">{e.ref}</span>
          <span className="text-fg-muted">{e.detail}</span>
        </li>
      ))}
    </ul>
  );
}

export function AnswerCard({ a }: { a: AssistantAnswer }) {
  const t = useT();
  return (
    <div className="space-y-2 rounded-md border border-ai/25 bg-ai/5 p-2.5">
      <div className="flex items-center gap-1.5">
        <AIBadge />
        <ConfidenceBadge score={a.confidence} />
        {a.fromCache && <span className="text-[10px] text-fg-subtle">{t("ai.cached")}</span>}
        {a.rejectedEvidence > 0 && <span className="text-[10px] text-warn" title={t("ai.rejectedTip")}>{t("ai.rejectedRefs", { n: a.rejectedEvidence })}</span>}
      </div>
      <p className="whitespace-pre-wrap text-xs leading-relaxed">{a.answer}</p>
      {a.evidence.length > 0 && <div><SectionTitle className="mb-1">{t("ai.evidence")}</SectionTitle><EvidenceList evidence={a.evidence} /></div>}
      {a.assumptions.length > 0 && <List title={t("ai.assumptions")} items={a.assumptions} />}
      {a.unknowns.length > 0 && <List title={t("ai.unknown")} items={a.unknowns} tone="text-unknown" />}
      {a.suggestedChecks.length > 0 && <List title={t("ai.whatToCheck")} items={a.suggestedChecks} tone="text-calc" />}
    </div>
  );
}

function List({ title, items, tone }: { title: string; items: string[]; tone?: string }) {
  return (
    <div>
      <SectionTitle className="mb-0.5">{title}</SectionTitle>
      <ul className="list-disc space-y-0.5 pl-4 text-[11px] text-fg-muted">
        {items.map((x, i) => <li key={i} className={tone}>{x}</li>)}
      </ul>
    </div>
  );
}
