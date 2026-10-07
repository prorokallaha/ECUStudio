"use client";
import { useEffect, useRef, useState } from "react";
import { Bot, Paperclip, Send, X } from "lucide-react";
import type { AssistantAnswer } from "@/types/domain";
import { api, ApiError } from "@/services/api";
import { useInfo } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { describeSelection, useSelection } from "@/stores/selection";
import { useUI } from "@/stores/ui";
import { Button, EmptyState, Spinner } from "@/components/ui";
import { AnswerCard } from "./answer-card";
import { useT, type TKey } from "@/i18n";

interface Turn { q: string; context: string; a?: AssistantAnswer; error?: string }

const SUGGESTIONS: TKey[] = ["ai.suggestions.s1", "ai.suggestions.s2", "ai.suggestions.s3", "ai.suggestions.s4"];

/**
 * Contextual AI panel. It sends only the analysis id, the question and the current selection
 * (map cells / operating point / component). The server builds the structured context; the binary is never sent.
 */
export function AIPanel() {
  const t = useT();
  const { analysisId } = useWorkspace();
  const info = useInfo();
  const selection = useSelection((s) => s.selection);
  const clearSel = useSelection((s) => s.select);
  const close = useUI((s) => s.setAIPanel);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [q, setQ] = useState("");
  const [busy, setBusy] = useState(false);
  const scroller = useRef<HTMLDivElement>(null);

  useEffect(() => { setTurns([]); }, [analysisId]);
  useEffect(() => { scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: "smooth" }); }, [turns]);

  async function ask(question: string) {
    if (!analysisId || !question.trim() || busy) return;
    const turn: Turn = { q: question.trim(), context: describeSelection(selection) };
    setTurns((ts) => [...ts, turn]);
    setQ("");
    setBusy(true);
    try {
      const a = await api.analyses.ask(analysisId, turn.q, selection ?? undefined);
      setTurns((ts) => ts.map((x) => (x === turn ? { ...x, a } : x)));
    } catch (e) {
      const msg = e instanceof ApiError ? `${e.code}: ${e.message}` : String(e);
      setTurns((ts) => ts.map((x) => (x === turn ? { ...x, error: msg } : x)));
    } finally {
      setBusy(false);
    }
  }

  const configured = info.data?.aiConfigured;
  return (
    <aside className="flex h-full flex-col bg-bg-elev">
      <div className="flex h-9 items-center gap-2 border-b border-border px-3">
        <Bot className="size-4 text-ai" />
        <span className="text-xs font-semibold uppercase tracking-wide text-fg-muted">{t("nav.ai")}</span>
        <span className={`ml-1 size-1.5 rounded-full ${configured ? "bg-ok" : "bg-unknown"}`} title={configured ? t("ai.configured") : t("ai.noKey")} />
        <button className="ml-auto text-fg-subtle hover:text-fg" onClick={() => close(false)} aria-label={t("ai.closePanel")}><X className="size-3.5" /></button>
      </div>

      <div className="flex items-center gap-1.5 border-b border-border px-3 py-1.5 text-[11px]">
        <Paperclip className="size-3 text-ai" />
        <span className="truncate text-fg-muted" title={t("ai.attached")}>{describeSelection(selection)}</span>
        {selection && <button className="ml-auto text-fg-subtle hover:text-fg" onClick={() => clearSel(null)} title={t("ai.detach")}><X className="size-3" /></button>}
      </div>

      <div ref={scroller} className="flex-1 space-y-3 overflow-y-auto p-3">
        {!analysisId && <EmptyState title={t("ai.noAnalysis")}>{t("ai.noAnalysisHint")}</EmptyState>}
        {analysisId && configured === false && (
          <div className="rounded-md border border-border bg-panel p-3 text-xs text-fg-muted">
            {t("ai.disabledPre")} <code className="text-fg">ANTHROPIC_API_KEY</code> {t("ai.disabledPost")}
          </div>
        )}
        {analysisId && turns.length === 0 && configured && (
          <div className="space-y-1.5">
            <div className="text-[11px] text-fg-subtle">{t("ai.intro")}</div>
            {SUGGESTIONS.map((s) => (
              <button key={s} onClick={() => ask(t(s))} className="block w-full rounded-md border border-border px-2.5 py-1.5 text-left text-xs text-fg-muted hover:border-ai/40 hover:text-fg">{t(s)}</button>
            ))}
          </div>
        )}
        {turns.map((turn, i) => (
          <div key={i} className="space-y-1.5">
            <div className="ml-6 rounded-md bg-panel-2 px-2.5 py-1.5 text-xs">
              {turn.q}
              <div className="mt-0.5 text-[10px] text-fg-subtle">{t("ai.context", { c: turn.context })}</div>
            </div>
            {turn.a ? <AnswerCard a={turn.a} /> : turn.error ? <div className="rounded-md border border-danger/30 bg-danger/10 p-2 text-xs text-danger">{turn.error}</div> : <div className="flex items-center gap-2 text-xs text-fg-muted"><Spinner className="text-ai" />{t("ai.thinking")}</div>}
          </div>
        ))}
      </div>

      <form className="border-t border-border p-2" onSubmit={(e) => { e.preventDefault(); ask(q); }}>
        <div className="flex items-end gap-1.5">
          <textarea
            value={q}
            onChange={(e) => setQ(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); ask(q); } }}
            rows={2}
            maxLength={4000}
            disabled={!analysisId || !configured}
            placeholder={configured ? t("ai.placeholder") : t("ai.notConfigured")}
            className="min-h-[52px] flex-1 resize-none rounded-md border border-border bg-bg px-2 py-1.5 text-xs outline-none focus:border-ai disabled:opacity-50"
          />
          <Button type="submit" size="icon" variant="ai" disabled={!q.trim() || busy || !configured}><Send className="size-3.5" /></Button>
        </div>
      </form>
    </aside>
  );
}
