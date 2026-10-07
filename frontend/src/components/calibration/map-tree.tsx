"use client";
import { memo, useMemo, useState, type ReactNode } from "react";
import { ChevronDown, ChevronRight, HelpCircle, Pencil, Search } from "lucide-react";
import type { AnalysisReport, MapCandidate, MapCategory, MapSummary } from "@/types/domain";
import type { MapTreeFilter } from "@/types/maps-edit";
import { Input } from "@/components/ui";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";
import { candidateLabel, isWeakMap, mapLabel, roleCounts } from "./map-label";

const CATS: MapCategory[] = ["Torque", "Fuel", "Air", "Protection", "Unknown"];
const FILTERS: MapTreeFilter[] = ["all", "defined", "modified", "candidates", "unknown", "user"];

type Item =
  | { kind: "map"; m: MapSummary; label: string }
  | { kind: "candidate"; c: MapCandidate; label: string };

/**
 * Map tree of the maps workspace: categories of identified maps plus filter groups (defined / modified / candidates /
 * unknown / user). Weak hypotheses only appear as "Неизвестная карта · Кандидат: …", never as the map's name.
 */
export const MapTree = memo(function MapTree({ r, edited, activeMap, activeCandidate, onMap, onCandidate }: {
  r: AnalysisReport; edited: Set<string>; activeMap?: string | null; activeCandidate?: string | null;
  onMap: (id: string) => void; onCandidate: (id: string) => void;
}) {
  const t = useT();
  const [q, setQ] = useState("");
  const [filter, setFilter] = useState<MapTreeFilter>("all");
  const [closed, setClosed] = useState<Record<string, boolean>>({});

  const all = useMemo(() => {
    const rc = roleCounts(r.maps);
    const maps: Item[] = r.maps.map((m) => ({ kind: "map", m, label: mapLabel(t, m, rc) }));
    const cands: Item[] = r.candidates.map((c) => ({ kind: "candidate", c, label: candidateLabel(t, c) }));
    return [...maps, ...cands];
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [r.maps, r.candidates, t.lang]);

  const inFilter = (it: Item, f: MapTreeFilter): boolean => {
    if (f === "all") return true;
    if (it.kind === "map") {
      const m = it.m;
      switch (f) {
        case "defined": return !isWeakMap(m) && m.role !== "Unknown";
        case "modified": return m.modified || edited.has(m.id);
        case "candidates": return isWeakMap(m) && m.role !== "Unknown";
        case "unknown": return m.role === "Unknown";
        case "user": return m.source === "User";
      }
    } else {
      const c = it.c;
      switch (f) {
        case "defined": return false;
        case "modified": return !!c.change?.isModified;
        case "candidates": return c.status === "Candidate" && !!c.best && c.best.role !== "Unknown";
        case "unknown": return c.status === "Rejected" || !c.best || c.best.role === "Unknown";
        case "user": return c.status === "Confirmed";
      }
    }
    return false;
  };

  const counts = useMemo(() => Object.fromEntries(FILTERS.map((f) => [f, all.filter((it) => inFilter(it, f)).length])) as Record<MapTreeFilter, number>,
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [all, edited]);

  const needle = q.trim().toLowerCase();
  const visible = all.filter((it) => inFilter(it, filter) && (!needle || (it.kind === "map"
    ? `${it.label} ${it.m.name} ${it.m.id} ${hex(it.m.address)}`
    : `${it.label} ${it.c.id} ${hex(it.c.address)}`).toLowerCase().includes(needle)));
  const strong = visible.filter((it): it is Extract<Item, { kind: "map" }> => it.kind === "map" && !isWeakMap(it.m));
  const weak = visible.filter((it) => it.kind === "candidate" || isWeakMap(it.m));

  const header = (key: string, label: string, n: number, tone?: string, icon?: ReactNode) => (
    <button onClick={() => setClosed((s) => ({ ...s, [key]: !s[key] }))} className={cn("flex w-full items-center gap-1 px-2 py-1 text-[10px] font-semibold uppercase tracking-wider hover:text-fg", tone ?? "text-fg-subtle")}>
      {closed[key] ? <ChevronRight className="size-3" /> : <ChevronDown className="size-3" />}{icon}{label}<span className="ml-auto font-normal text-fg-subtle">{n}</span>
    </button>
  );

  return (
    <div className="flex h-full flex-col">
      <div className="space-y-1.5 border-b border-border p-2">
        <div className="relative">
          <Search className="absolute left-2 top-1.5 size-3.5 text-fg-subtle" />
          <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder={t("maps.search")} className="pl-7" />
        </div>
        <div className="flex flex-wrap gap-1">
          {FILTERS.map((f) => (
            <button key={f} onClick={() => setFilter(f)} disabled={f !== "all" && counts[f] === 0}
              className={cn("rounded border px-1.5 py-0.5 text-[10px] transition-colors disabled:opacity-40",
                filter === f ? "border-calc/40 bg-calc/15 text-fg" : "border-border text-fg-muted hover:text-fg")}>
              {t(`mapEditor.filter.${f}`)} <span className="num text-fg-subtle">{counts[f]}</span>
            </button>
          ))}
        </div>
      </div>
      <div className="flex-1 overflow-y-auto py-1 text-xs">
        {CATS.map((cat) => {
          const items = strong.filter((it) => it.m.category === cat);
          if (!items.length) return null;
          return (
            <div key={cat}>
              {header(cat, t.tx(`mapCategory.${cat}`, cat), items.length)}
              {!closed[cat] && items.map((it) => (
                <MapRow key={it.m.id} m={it.m} label={it.label} active={activeMap === it.m.id} edited={edited.has(it.m.id)} onClick={onMap} />
              ))}
            </div>
          );
        })}
        {weak.length > 0 && (
          <div className="mt-1 border-t border-border pt-1">
            {header("__weak", t("mapEditor.treeCandidates"), weak.length, "text-ai", <HelpCircle className="size-3" />)}
            {!closed.__weak && weak.map((it) => it.kind === "map"
              ? <MapRow key={it.m.id} m={it.m} label={it.label} active={activeMap === it.m.id} edited={edited.has(it.m.id)} onClick={onMap} weak />
              : (
                <button key={it.c.id} onClick={() => onCandidate(it.c.id)} title={`${it.label} · ${hex(it.c.address)}`}
                  className={cn("flex w-full items-center gap-2 py-1 pl-6 pr-2 text-left hover:bg-panel-2", activeCandidate === it.c.id && "bg-ai/12")}>
                  <span className={cn("size-1.5 shrink-0 rounded-full", it.c.status === "Confirmed" ? "bg-ok" : it.c.status === "Rejected" ? "bg-danger" : "bg-ai")} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate">{it.label}</span>
                    <span className="num block text-[10px] text-fg-subtle">{hex(it.c.address)} · {it.c.rows}×{it.c.cols} · {t.tx(`candidateStatus.${it.c.status}`, it.c.status)}
                      {it.c.change?.isModified && <span className="text-warn"> · {t("mapEditor.candidateChanged", { n: it.c.change.changedCells, pct: `${it.c.change.meanDeltaPct > 0 ? "+" : ""}${it.c.change.meanDeltaPct}` })}</span>}</span>
                  </span>
                </button>
              ))}
          </div>
        )}
        {visible.length === 0 && <div className="px-3 py-4 text-center text-[11px] text-fg-subtle">{t("mapEditor.treeEmpty")}</div>}
      </div>
    </div>
  );
});

const MapRow = memo(function MapRow({ m, label, active, edited, weak, onClick }: {
  m: MapSummary; label: string; active: boolean; edited: boolean; weak?: boolean; onClick: (id: string) => void;
}) {
  const t = useT();
  return (
    <button onClick={() => onClick(m.id)} title={`${label} · ${m.name} · ${hex(m.address)}`}
      className={cn("flex w-full items-center gap-2 py-1 pl-6 pr-2 text-left hover:bg-panel-2", active && (weak ? "bg-ai/12" : "bg-calc/12 text-fg"))}>
      <span className={cn("size-1.5 shrink-0 rounded-full", weak ? "bg-ai" : m.modified ? "bg-calc" : "bg-ok/60")} title={m.modified ? t("maps.dotModified") : t("maps.dotUnchanged")} />
      <span className={cn("flex-1 truncate", weak && "text-fg-muted")}>{label}</span>
      {edited && <Pencil className="size-3 shrink-0 text-attn" aria-label={t("mapEditor.editedNotAnalysed")} />}
      {m.modified && <span className="num text-[10px] text-warn">{m.maxDeltaPct > 0 ? "+" : ""}{m.maxDeltaPct.toFixed(0)}%</span>}
    </button>
  );
});
