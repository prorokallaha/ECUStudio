"use client";
import type { AnalysisReport } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Kbd, Select } from "@/components/ui";
import { DependencyGraphView } from "@/components/calibration/dependency-graph";
import { useWorkspace } from "@/hooks/use-workspace";
import { nodeStateTone, toneBg } from "@/lib/colors";
import { cn } from "@/lib/cn";

export function DependenciesPage() {
  return <WithReport>{(r) => <Deps r={r} />}</WithReport>;
}

function Deps({ r }: { r: AnalysisReport }) {
  const { params, setParam, router, href } = useWorkspace();
  const focus = params.get("focus");
  return (
    <div className="flex h-full flex-col">
      <PageHeader
        title="Dependencies"
        subtitle="ECU torque, fuel and air path rebuilt from identified maps. Double-click a map to open it."
        actions={<>
          <Select value={focus ?? ""} onChange={(e) => setParam("focus", e.target.value || null)}>
            <option value="">Focus: whole graph</option>
            {r.dependencies.nodes.filter((n) => n.kind === "Map").map((n) => <option key={n.id} value={n.id}>{n.label}</option>)}
          </Select>
          <span className="text-[11px] text-fg-subtle"><Kbd>F</Kbd> fit</span>
        </>}
      />
      <div className="flex items-center gap-3 border-b border-border px-4 py-1.5 text-[11px] text-fg-muted">
        {(["Stock", "Modified", "Warning", "Danger", "Unknown", "NotApplicable"] as const).map((s) => (
          <span key={s} className="flex items-center gap-1"><span className={cn("size-2 rounded-sm", toneBg[nodeStateTone[s]])} />{s === "NotApplicable" ? "N/A" : s}</span>
        ))}
      </div>
      <div className="min-h-0 flex-1">
        <DependencyGraphView graph={r.dependencies} focus={focus} onOpenMap={(id) => router.push(href(`maps/${id}`))} />
      </div>
    </div>
  );
}
