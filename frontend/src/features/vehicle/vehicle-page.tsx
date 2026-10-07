"use client";
import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Pencil, RotateCcw } from "lucide-react";
import { toast } from "sonner";
import type { ComponentKind, ComponentSpec, HardwareOverride, Project } from "@/types/domain";
import { api, ApiError } from "@/services/api";
import { keys, useProject, useReport, useRunAnalysis } from "@/hooks/use-analysis";
import { useWorkspace } from "@/hooks/use-workspace";
import { useHistory } from "@/stores/history";
import { PageHeader, PageSkeleton } from "@/components/layout/page";
import { Badge, Button, Card, CardHeader, ConfidenceBadge, Input, SectionTitle, Select, SourceBadge } from "@/components/ui";
import { ParamTable } from "@/components/vehicle/param-table";
import { OverrideDialog } from "./override-dialog";
import { FilesCard } from "./files-card";
import { fmtParam } from "@/lib/format";
import { cn } from "@/lib/cn";

const KINDS: { kind: ComponentKind; key: string; label: string }[] = [
  { kind: "Engine", key: "engine", label: "Engine" }, { kind: "Turbo", key: "turbo", label: "Turbo" },
  { kind: "Injectors", key: "injectors", label: "Injectors" }, { kind: "FuelSystem", key: "fuelSystem", label: "Fuel system" },
  { kind: "Transmission", key: "transmission", label: "Transmission" }, { kind: "Clutch", key: "clutch", label: "Clutch" },
  { kind: "Intercooler", key: "intercooler", label: "Intercooler" }, { kind: "Sensors", key: "sensors", label: "Sensors" },
  { kind: "Emissions", key: "emissions", label: "Emissions" },
];

export function VehiclePage() {
  const { projectId } = useWorkspace();
  const project = useProject(projectId);
  if (!project.data) return <PageSkeleton />;
  return <Vehicle project={project.data} />;
}

function Vehicle({ project }: { project: Project }) {
  const qc = useQueryClient();
  const report = useReport();
  const run = useRunAnalysis();
  const history = useHistory();
  const [vin, setVin] = useState(project.vin ?? "");
  const [selected, setSelected] = useState<string>("turbo");
  const [dialogKind, setDialogKind] = useState<ComponentKind | null>(null);
  useEffect(() => setVin(project.vin ?? ""), [project.vin]);

  const hw = report.data?.vehicle.profile.hardware as Record<string, ComponentSpec> | undefined;
  const refresh = () => qc.invalidateQueries({ queryKey: keys.project(project.id) });

  // Autosave: VIN is persisted on blur; analysis is recomputed because the VIN changes variant probabilities.
  const saveVin = async () => {
    const v = vin.trim().toUpperCase() || null;
    if (v === (project.vin ?? null)) return;
    try {
      await api.projects.update(project.id, { vin: v });
      refresh();
      toast.success("VIN saved — recomputing");
      run.mutate({ projectId: project.id });
    } catch (e) { toast.error(e instanceof ApiError ? e.message : String(e)); }
  };

  const applyOverrides = async (next: HardwareOverride[]) => { await api.projects.setHardware(project.id, next); refresh(); };

  const override = async (kind: ComponentKind, catalogId: string, note: string) => {
    const before = project.hardwareOverrides ?? [];
    const after = [...before.filter((o) => o.kind !== kind), { kind, catalogId, note: note || null } as HardwareOverride];
    await history.execute({
      label: `${kind} → ${catalogId}`,
      run: async () => { await applyOverrides(after); run.mutate({ projectId: project.id }); },
      undo: async () => { await applyOverrides(before); run.mutate({ projectId: project.id }); },
    });
  };

  const reset = async (kind: ComponentKind) => {
    const before = project.hardwareOverrides ?? [];
    await history.execute({
      label: `Reset ${kind}`,
      run: async () => { await api.projects.resetHardware(project.id, kind); refresh(); run.mutate({ projectId: project.id }); },
      undo: async () => { await applyOverrides(before); run.mutate({ projectId: project.id }); },
    });
  };

  const candidates = report.data?.vehicle.candidates ?? [];
  const variant = candidates.find((c) => c.variant.id === report.data?.vehicle.profile.variantId)?.variant;
  const spec = hw?.[selected];
  const kindOf = KINDS.find((k) => k.key === selected)!;

  return (
    <div>
      <PageHeader title="Vehicle" subtitle="VIN is evidence, not proof of installed hardware. Override what you have verified." />
      <div className="space-y-4 p-4">
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
          <Card>
            <CardHeader title="Identification" />
            <div className="space-y-2.5 p-3 text-xs">
              <label className="block space-y-1">
                <SectionTitle>VIN</SectionTitle>
                <Input value={vin} maxLength={17} onChange={(e) => setVin(e.target.value.toUpperCase())} onBlur={saveVin} onKeyDown={(e) => e.key === "Enter" && saveVin()} className="num" placeholder="17 characters" />
              </label>
              {report.data?.vehicle.vin && (
                <div className="space-y-0.5 text-[11px] text-fg-muted">
                  <div>{report.data.vehicle.vin.manufacturer} · {report.data.vehicle.vin.region} · MY {report.data.vehicle.vin.modelYear ?? "?"} · platform {report.data.vehicle.vin.platformCode ?? "?"}</div>
                  {(report.data.vehicle.vin.warnings ?? []).map((w, i) => <div key={i} className="text-attn">⚠ {w}</div>)}
                </div>
              )}
              {report.data && (["make", "model", "modelYear", "engineCode", "platform"] as const).map((k) => {
                const p = report.data!.vehicle.profile[k];
                return (
                  <div key={k} className="flex items-center justify-between gap-2">
                    <span className="text-fg-subtle capitalize">{k.replace(/([A-Z])/g, " $1")}</span>
                    <span className="flex items-center gap-1.5"><span className={fmtParam(p) === "UNKNOWN" ? "text-unknown font-semibold" : ""}>{fmtParam(p)}</span>{p && <SourceBadge source={p.source} />}{p && <ConfidenceBadge score={p.confidence} showScore={false} />}</span>
                  </div>
                );
              })}
            </div>
          </Card>
          <Card className="xl:col-span-2">
            <CardHeader title="Variant candidates" subtitle="ranked from VIN + ECU identification" />
            <table className="w-full text-xs">
              <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>Variant</th><th>Engine</th><th>Years</th><th>Probability</th><th>Why</th><th /></tr></thead>
              <tbody>
                {candidates.slice(0, 6).map((c) => {
                  const active = c.variant.id === report.data?.vehicle.profile.variantId;
                  return (
                    <tr key={c.variant.id} className={cn("border-t border-border [&>td]:px-3 [&>td]:py-1.5", active && "bg-calc/8")}>
                      <td className="font-medium">{c.variant.make} {c.variant.model}</td>
                      <td>{c.variant.engineCode}</td>
                      <td className="num text-fg-muted">{c.variant.yearFrom}–{c.variant.yearTo}</td>
                      <td><div className="flex items-center gap-1.5"><div className="h-1.5 w-16 rounded-full bg-panel-2"><div className="h-full rounded-full bg-calc" style={{ width: `${c.probability * 100}%` }} /></div><span className="num">{(c.probability * 100).toFixed(0)}%</span></div></td>
                      <td className="max-w-64 truncate text-fg-muted" title={c.reasons.join("; ")}>{c.reasons.join("; ")}</td>
                      <td className="text-right">{active ? <Badge tone="calc">in use</Badge> : <Button size="xs" variant="ghost" onClick={async () => { await api.projects.update(project.id, { preferredVariantId: c.variant.id }); refresh(); run.mutate({ projectId: project.id }); }}>Use</Button>}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {variant?.transmissionOptions?.length ? (
              <div className="flex items-center gap-2 border-t border-border px-3 py-2 text-xs">
                <span className="text-fg-subtle">Transmission</span>
                <Select value={project.transmissionId ?? ""} onChange={async (e) => { await api.projects.update(project.id, { transmissionId: e.target.value || null }); refresh(); run.mutate({ projectId: project.id }); }}>
                  <option value="">Variant default</option>
                  {variant.transmissionOptions.map((t) => <option key={t} value={t}>{t}</option>)}
                </Select>
              </div>
            ) : null}
          </Card>
        </div>

        <FilesCard project={project} />

        <Card>
          <CardHeader title="Hardware profile" subtitle="Simulation and risk use these limits. Unknown limits stay UNKNOWN — they are never guessed." />
          {!hw ? <div className="px-3 py-6 text-xs text-fg-muted">Run an analysis to resolve the hardware profile.</div> : (
            <div className="grid grid-cols-1 xl:grid-cols-[360px_1fr]">
              <div className="border-r border-border">
                {KINDS.map((k) => {
                  const c = hw[k.key];
                  if (!c) return null;
                  return (
                    <button key={k.key} onClick={() => setSelected(k.key)} className={cn("flex w-full items-center gap-2 border-b border-border px-3 py-2 text-left text-xs hover:bg-panel-2", selected === k.key && "bg-calc/10")}>
                      <span className="w-24 shrink-0 text-fg-subtle">{k.label}</span>
                      <span className="flex-1 truncate font-medium">{c.name}</span>
                      {c.isOverride && <Badge tone="attn">override</Badge>}
                      {c.userVerified && <CheckCircle2 className="size-3.5 text-ok" />}
                      <SourceBadge source={c.source} />
                    </button>
                  );
                })}
              </div>
              <div>
                {spec && (
                  <>
                    <div className="flex items-center gap-2 border-b border-border px-3 py-2">
                      <div className="text-sm font-semibold">{spec.name}</div>
                      <ConfidenceBadge score={spec.confidence} />
                      {spec.note && <span className="truncate text-[11px] text-fg-muted">{spec.note}</span>}
                      <div className="ml-auto flex gap-1">
                        {spec.isOverride && <Button size="xs" variant="ghost" onClick={() => reset(kindOf.kind)}><RotateCcw className="size-3" />Reset</Button>}
                        <Button size="xs" onClick={() => setDialogKind(kindOf.kind)}><Pencil className="size-3" />Override</Button>
                      </div>
                    </div>
                    <ParamTable spec={spec} />
                  </>
                )}
              </div>
            </div>
          )}
        </Card>
      </div>
      <OverrideDialog kind={dialogKind} current={dialogKind ? hw?.[KINDS.find((k) => k.kind === dialogKind)!.key] : undefined} open={!!dialogKind} onClose={() => setDialogKind(null)} onApply={(id, note) => override(dialogKind!, id, note)} />
    </div>
  );
}
