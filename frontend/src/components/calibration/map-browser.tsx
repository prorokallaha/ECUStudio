"use client";
import { useMemo, useState } from "react";
import { ChevronDown, ChevronRight, HelpCircle, Search } from "lucide-react";
import type { AnalysisReport, MapCategory } from "@/types/domain";
import { Input, Segmented } from "@/components/ui";
import { hex } from "@/lib/format";
import { cn } from "@/lib/cn";
import { useT } from "@/i18n";

const CATS: MapCategory[] = ["Torque", "Fuel", "Air", "Protection", "Unknown"];

/** Category tree of identified maps + the unknown-candidate queue. */
export function MapBrowser({ r, activeMap, activeCandidate, onMap, onCandidate }: {
  r: AnalysisReport; activeMap?: string | null; activeCandidate?: string | null; onMap: (id: string) => void; onCandidate: (id: string) => void;
}) {
  const t = useT();
  const [q, setQ] = useState("");
  const [filter, setFilter] = useState<"all" | "modified">("all");
  const [closed, setClosed] = useState<Record<string, boolean>>({});
  const maps = useMemo(() => r.maps.filter((m) =>
    (filter === "all" || m.modified) &&
    (!q || `${m.name} ${m.role} ${m.id} ${hex(m.address)}`.toLowerCase().includes(q.toLowerCase()))), [r.maps, q, filter]);
  const candidates = r.candidates.filter((c) => !q || `${c.id} ${hex(c.address)} ${c.best?.role ?? ""}`.toLowerCase().includes(q.toLowerCase()));

  return (
    <div className="flex h-full flex-col">
      <div className="space-y-1.5 border-b border-border p-2">
        <div className="relative">
          <Search className="absolute left-2 top-1.5 size-3.5 text-fg-subtle" />
          <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder={t("maps.search")} className="pl-7" />
        </div>
        <Segmented size="xs" value={filter} onChange={setFilter} options={[{ value: "all", label: t("maps.all", { n: r.maps.length }) }, { value: "modified", label: t("maps.modified", { n: r.maps.filter((m) => m.modified).length }) }]} />
      </div>
      <div className="flex-1 overflow-y-auto py-1 text-xs">
        {CATS.map((cat) => {
          const items = maps.filter((m) => m.category === cat);
          if (!items.length) return null;
          return (
            <div key={cat}>
              <button onClick={() => setClosed((s) => ({ ...s, [cat]: !s[cat] }))} className="flex w-full items-center gap-1 px-2 py-1 text-[10px] font-semibold uppercase tracking-wider text-fg-subtle hover:text-fg">
                {closed[cat] ? <ChevronRight className="size-3" /> : <ChevronDown className="size-3" />}{t.tx(`mapCategory.${cat}`, cat)}<span className="ml-auto font-normal">{items.length}</span>
              </button>
              {!closed[cat] && items.map((m) => (
                <button key={m.id} onClick={() => onMap(m.id)} className={cn("flex w-full items-center gap-2 py-1 pl-6 pr-2 text-left hover:bg-panel-2", activeMap === m.id && "bg-calc/12 text-fg")}>
                  <span className={cn("size-1.5 shrink-0 rounded-full", m.modified ? "bg-calc" : "bg-ok/60")} title={m.modified ? t("maps.dotModified") : t("maps.dotUnchanged")} />
                  <span className="flex-1 truncate">{m.name}</span>
                  {m.modified && <span className="num text-[10px] text-warn">{m.maxDeltaPct > 0 ? "+" : ""}{m.maxDeltaPct.toFixed(0)}%</span>}
                </button>
              ))}
            </div>
          );
        })}
        <div className="mt-1 border-t border-border pt-1">
          <div className="flex items-center gap-1 px-2 py-1 text-[10px] font-semibold uppercase tracking-wider text-ai"><HelpCircle className="size-3" />{t("maps.unknownMaps")}<span className="ml-auto font-normal text-fg-subtle">{candidates.length}</span></div>
          {candidates.map((c) => (
            <button key={c.id} onClick={() => onCandidate(c.id)} className={cn("flex w-full items-center gap-2 py-1 pl-6 pr-2 text-left hover:bg-panel-2", activeCandidate === c.id && "bg-ai/12")}>
              <span className={cn("size-1.5 shrink-0 rounded-full", c.status === "Confirmed" ? "bg-ok" : c.status === "Rejected" ? "bg-danger" : "bg-ai")} />
              <span className="num">{hex(c.address)}</span>
              <span className="text-fg-subtle">{c.rows}×{c.cols}</span>
              <span className="ml-auto truncate text-[10px] text-fg-muted">{(() => { const role = c.status === "Confirmed" ? c.confirmedRole : c.best?.role; return role ? t.tx(`role.${role}`, role) : null; })()}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
