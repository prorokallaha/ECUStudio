"use client";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Bot, Play } from "lucide-react";
import { useState } from "react";
import { toast } from "sonner";
import type { AnalysisReport } from "@/types/domain";
import { api, ApiError } from "@/services/api";
import { useInfo } from "@/hooks/use-analysis";
import { useJobs } from "@/stores/jobs";
import { PageHeader, WithReport } from "@/components/layout/page";
import { AIBadge, Badge, Button, Card, CardHeader, ConfidenceBadge, EmptyState, SectionTitle, SeverityBadge } from "@/components/ui";
import { EvidenceList } from "@/components/ai/answer-card";
import { useT } from "@/i18n";

export function AiPage() {
  return <WithReport>{(r) => <AI r={r} />}</WithReport>;
}

function AI({ r }: { r: AnalysisReport }) {
  const t = useT();
  const qc = useQueryClient();
  const info = useInfo();
  const track = useJobs((s) => s.track);
  const apply = useJobs((s) => s.apply);
  const [running, setRunning] = useState(false);
  const result = useQuery({ queryKey: ["ai", r.id], queryFn: () => api.analyses.aiResult(r.id), retry: false });

  const start = async () => {
    setRunning(true);
    try {
      const { jobId } = await api.analyses.startAI(r.id);
      track({ jobId, kind: "ai", analysisId: r.id });
      api.jobs.subscribe(jobId, (e) => {
        apply(e);
        if (e.status === "Completed") { setRunning(false); qc.invalidateQueries({ queryKey: ["ai", r.id] }); }
        if (e.status === "Failed") { setRunning(false); toast.error(e.error ?? t("errors.aiFailed")); }
      }, () => setRunning(false));
    } catch (e) { setRunning(false); toast.error(e instanceof ApiError ? `${e.code}: ${e.message}` : String(e)); }
  };

  const a = result.data;
  return (
    <div>
      <PageHeader
        title={t("nav.ai")}
        subtitle={t("ai.subtitle")}
        actions={<Button variant="ai" disabled={!info.data?.aiConfigured || running} onClick={start}><Play className="size-3.5" />{a ? t("ai.rerunReview") : t("ai.runReview")}</Button>}
      />
      <div className="space-y-4 p-4">
        {!info.data?.aiConfigured && <Card className="p-4 text-xs text-fg-muted">{t("ai.notConfiguredPre")} <code className="text-fg">ANTHROPIC_API_KEY</code>{t("ai.notConfiguredPost")}</Card>}
        {!a && info.data?.aiConfigured && !running && <EmptyState icon={<Bot className="size-8 text-ai" />} title={t("ai.noReview")}>{t("ai.noReviewHint")}</EmptyState>}
        {a && (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <span className="text-xs text-fg-subtle">{t("ai.verdict")}</span><SeverityBadge severity={a.verdict} /><ConfidenceBadge score={a.confidence} />
              <span className="text-[11px] text-fg-subtle">{t("ai.usage", { model: a.model, in: a.usage.inputTokens, out: a.usage.outputTokens, cache: a.usage.cacheReadTokens })}</span>
            </div>
            <Card>
              <CardHeader title={t("ai.consensus")} subtitle={t("ai.consensusSubtitle")} />
              <table className="w-full text-xs">
                <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>{t("ai.colComponent")}</th><th>{t("ai.colPhysics")}</th><th>{t("ai.colFinal")}</th><th>{t("ai.colAgreement")}</th><th>{t("ai.colVotes")}</th><th>{t("ai.colRationale")}</th></tr></thead>
                <tbody>
                  {a.consensus.map((c) => (
                    <tr key={c.component} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5">
                      <td className="font-medium">{c.component}</td><td><SeverityBadge severity={c.physicsSeverity} /></td><td><SeverityBadge severity={c.finalSeverity} /></td>
                      <td className="num">{(c.agreement * 100).toFixed(0)}%</td>
                      <td className="text-[11px] text-fg-muted">{c.votes.map((v) => `${v.source}:${t.tx(`severity.${v.severity}`, v.severity)}`).join(" · ")}</td>
                      <td className="text-[11px] text-fg-muted">{c.rationale}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>
            <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
              <Card>
                <CardHeader title={t("ai.accepted")} subtitle={`${a.findings.length}`} />
                {a.findings.map((f, i) => (
                  <div key={i} className="border-t border-border px-3 py-2 text-xs">
                    <div className="flex items-center gap-1.5"><AIBadge /><SeverityBadge severity={f.severity} /><span className="text-[10px] text-fg-subtle">{f.sourceDetail}</span><ConfidenceBadge score={f.confidence} className="ml-auto" /></div>
                    <div className="mt-1">{f.text}</div>
                    <div className="mt-1"><EvidenceList evidence={f.evidence} /></div>
                  </div>
                ))}
              </Card>
              <div className="space-y-4">
                <Card>
                  <CardHeader title={t("ai.verifier")} subtitle={t("ai.verifierSubtitle")} />
                  {a.verifications.map((v, i) => (
                    <div key={i} className="border-t border-border px-3 py-1.5 text-xs">
                      <div className="flex items-center gap-1.5"><Badge tone={v.verdict === "supported" ? "ok" : v.verdict === "refuted" ? "danger" : "attn"}>{t.tx(`ai.verdictValue.${String(v.verdict).toLowerCase()}`, v.verdict)}</Badge><span className="text-[10px] text-fg-subtle">{v.agent}</span></div>
                      <div className="mt-0.5 text-fg-muted">{v.claim}</div>
                      <div className="text-[11px] text-fg-subtle">{v.rationale}</div>
                    </div>
                  ))}
                  {!a.verifications.length && <div className="px-3 py-2 text-xs text-fg-muted">{t("ai.noVerification")}</div>}
                </Card>
                <Card>
                  <CardHeader title={t("ai.rejected")} subtitle={t("ai.rejectedSubtitle")} />
                  {a.rejected.map((x, i) => <div key={i} className="border-t border-border px-3 py-1.5 text-xs"><span className="text-[10px] text-fg-subtle">{x.agent}</span> <span className="text-fg-muted line-through">{x.text}</span><div className="text-[11px] text-warn">{x.reason}</div></div>)}
                  {!a.rejected.length && <div className="px-3 py-2 text-xs text-fg-muted">{t("common.none")}</div>}
                </Card>
                {(a.contradictions?.length ?? 0) > 0 && <Card className="p-3"><SectionTitle className="mb-1">{t("ai.contradictions")}</SectionTitle>{a.contradictions!.map((c, i) => <div key={i} className="text-xs text-warn">• {c}</div>)}</Card>}
                {(a.missingData?.length ?? 0) > 0 && <Card className="p-3"><SectionTitle className="mb-1">{t("ai.missingData")}</SectionTitle>{a.missingData!.map((c, i) => <div key={i} className="text-xs text-unknown">• {c}</div>)}</Card>}
                <Card>
                  <CardHeader title={t("ai.runs")} />
                  {a.runs.map((x) => <div key={x.agent} className="flex items-center gap-2 border-t border-border px-3 py-1 text-[11px]"><span className="w-28 font-medium">{x.agent}</span><span className="text-fg-muted">{t("ai.runStats", { a: x.accepted, r: x.rejected })}</span>{x.fromCache && <Badge tone="calc">{t("ai.cache")}</Badge>}{x.error && <span className="truncate text-danger">{x.error}</span>}</div>)}
                </Card>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
