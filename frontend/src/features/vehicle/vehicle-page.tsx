"use client";
import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Pencil, RotateCcw } from "lucide-react";
import { toast } from "sonner";
import type { ComponentKind, ComponentSpec, ConfigurationItem, HardwareOverride, Project, VehicleMatchStatus } from "@/types/domain";
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
import { useT } from "@/i18n";

const KINDS: { kind: ComponentKind; key: string }[] = [
  { kind: "Engine", key: "engine" }, { kind: "Turbo", key: "turbo" }, { kind: "Injectors", key: "injectors" }, { kind: "FuelSystem", key: "fuelSystem" },
  { kind: "Transmission", key: "transmission" }, { kind: "Clutch", key: "clutch" }, { kind: "Intercooler", key: "intercooler" },
  { kind: "Sensors", key: "sensors" }, { kind: "Emissions", key: "emissions" },
];

const STATUS_TONE: Record<VehicleMatchStatus, "ok" | "attn" | "warn" | "unknown"> = { Resolved: "ok", Ambiguous: "attn", NotInDatabase: "warn", NoData: "unknown" };
const ITEM_TONE = { Verified: "ok", Resolved: "calc", Ambiguous: "attn", Unknown: "unknown" } as const;

export function VehiclePage() {
  const { projectId } = useWorkspace();
  const project = useProject(projectId);
  if (!project.data) return <PageSkeleton />;
  return <Vehicle project={project.data} />;
}

function Vehicle({ project }: { project: Project }) {
  const t = useT();
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
      toast.success(t("vehicle.vinSaved"));
      run.mutate({ projectId: project.id });
    } catch (e) { toast.error(e instanceof ApiError ? e.message : String(e)); }
  };

  const applyOverrides = async (next: HardwareOverride[]) => { await api.projects.setHardware(project.id, next); refresh(); };

  const override = async (kind: ComponentKind, catalogId: string, note: string) => {
    const before = project.hardwareOverrides ?? [];
    const after = [...before.filter((o) => o.kind !== kind), { kind, catalogId, note: note || null } as HardwareOverride];
    await history.execute({
      label: t("vehicle.overrideLabel", { kind: t.tx(`vehicle.kind.${kind}`, kind), id: catalogId }),
      run: async () => { await applyOverrides(after); run.mutate({ projectId: project.id }); },
      undo: async () => { await applyOverrides(before); run.mutate({ projectId: project.id }); },
    });
  };

  const reset = async (kind: ComponentKind) => {
    const before = project.hardwareOverrides ?? [];
    await history.execute({
      label: t("vehicle.resetLabel", { kind: t.tx(`vehicle.kind.${kind}`, kind) }),
      run: async () => { await api.projects.resetHardware(project.id, kind); refresh(); run.mutate({ projectId: project.id }); },
      undo: async () => { await applyOverrides(before); run.mutate({ projectId: project.id }); },
    });
  };

  const candidates = report.data?.vehicle.candidates ?? [];
  const variant = candidates.find((c) => c.variant.id === report.data?.vehicle.profile.variantId)?.variant;
  const spec = hw?.[selected];
  const kindOf = KINDS.find((k) => k.key === selected)!;
  const status = report.data?.vehicle.status;
  const unlisted = report.data?.vehicle.unlistedProbability ?? 0;
  const config = report.data?.vehicle.configuration ?? [];

  return (
    <div>
      <PageHeader title={t("vehicle.title")} subtitle={t("vehicle.subtitle")} />
      <div className="space-y-4 p-4">
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
          <Card>
            <CardHeader title={t("vehicle.identification")} actions={status && <Badge tone={STATUS_TONE[status]} title={t.tx(`vehicle.statusHint.${status}`)}>{t.tx(`vehicle.status.${status}`, status)}</Badge>} />
            <div className="space-y-2.5 p-3 text-xs">
              <label className="block space-y-1">
                <SectionTitle>{t("vehicle.vin")}</SectionTitle>
                <Input value={vin} maxLength={17} onChange={(e) => setVin(e.target.value.toUpperCase())} onBlur={saveVin} onKeyDown={(e) => e.key === "Enter" && saveVin()} className="num" placeholder={t("vehicle.vinPlaceholder")} />
              </label>
              {report.data?.vehicle.vin && (
                <div className="space-y-0.5 text-[11px] text-fg-muted">
                  <div>{t("vehicle.vinLine", { manufacturer: report.data.vehicle.vin.manufacturer ?? "?", region: report.data.vehicle.vin.region ?? "?", year: report.data.vehicle.vin.modelYear ?? "?", platform: `${report.data.vehicle.vin.platformCode ?? "?"}${report.data.vehicle.vin.platformName ? ` (${report.data.vehicle.vin.platformName})` : ""}` })}</div>
                  {(report.data.vehicle.vin.warnings ?? []).map((w, i) => <div key={i} className="text-attn">⚠ {w}</div>)}
                </div>
              )}
              {report.data && (["make", "model", "modelYear", "engineCode", "platform"] as const).map((k) => {
                const p = report.data!.vehicle.profile[k];
                return (
                  <div key={k} className="flex items-center justify-between gap-2">
                    <span className="text-fg-subtle">{t.tx(`vehicle.${k}`, k)}</span>
                    <span className="flex items-center gap-1.5"><span className={fmtParam(p) === "UNKNOWN" ? "text-unknown font-semibold" : ""}>{fmtParam(p)}</span>{p && <SourceBadge source={p.source} />}{p && <ConfidenceBadge score={p.confidence} showScore={false} />}</span>
                  </div>
                );
              })}
              {status && <p className="text-[11px] text-fg-muted">{t.tx(`vehicle.statusHint.${status}`)}</p>}
              {(report.data?.vehicle.profile.notes ?? []).filter((n) => !n.startsWith("VIN does not encode")).map((n, i) => <div key={i} className="text-[11px] text-attn">⚠ {n}</div>)}
            </div>
          </Card>
          <Card className="xl:col-span-2">
            <CardHeader title={t("vehicle.candidates")} subtitle={t("vehicle.candidatesSubtitle")} actions={unlisted > 0.01 ? <Badge tone="unknown">{t("vehicle.notInDb", { p: `${Math.round(unlisted * 100)}%` })}</Badge> : null} />
            <table className="w-full text-xs">
              <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>{t("vehicle.variant")}</th><th>{t("vehicle.engine")}</th><th>{t("vehicle.years")}</th><th>{t("vehicle.probability")}</th><th>{t("vehicle.why")}</th><th /></tr></thead>
              <tbody>
                {candidates.slice(0, 6).map((c) => {
                  const active = c.variant.id === report.data?.vehicle.profile.variantId;
                  return (
                    <tr key={c.variant.id} className={cn("border-t border-border [&>td]:px-3 [&>td]:py-1.5", active && "bg-calc/8")}>
                      <td className="font-medium">{c.variant.make} {c.variant.model}</td>
                      <td>{c.variant.engineCode}</td>
                      <td className="num text-fg-muted">{c.variant.yearFrom}–{c.variant.yearTo}</td>
                      <td><div className="flex items-center gap-1.5"><div className="h-1.5 w-16 rounded-full bg-panel-2"><div className="h-full rounded-full bg-calc" style={{ width: `${c.probability * 100}%` }} /></div><span className="num">{(c.probability * 100).toFixed(0)}%</span></div></td>
                      <td className="max-w-80 text-fg-muted"><div className="truncate" title={c.reasons.join("; ")}>{c.reasons.join("; ")}</div>{c.conflicts.length > 0 && <div className="truncate text-warn" title={c.conflicts.join("; ")}>{t("vehicle.conflicts")}: {c.conflicts.join("; ")}</div>}</td>
                      <td className="text-right">{active ? <Badge tone="calc">{t("common.inUse")}</Badge> : <Button size="xs" variant="ghost" onClick={async () => { await api.projects.update(project.id, { preferredVariantId: c.variant.id }); refresh(); run.mutate({ projectId: project.id }); }}>{t("common.use")}</Button>}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {variant?.transmissionOptions?.length ? (
              <div className="flex items-center gap-2 border-t border-border px-3 py-2 text-xs">
                <span className="text-fg-subtle">{t("vehicle.transmission")}</span>
                <Select value={project.transmissionId ?? ""} onChange={async (e) => { await api.projects.update(project.id, { transmissionId: e.target.value || null }); refresh(); run.mutate({ projectId: project.id }); }}>
                  <option value="">{t("vehicle.variantDefault")}</option>
                  {variant.transmissionOptions.map((t) => <option key={t} value={t}>{t}</option>)}
                </Select>
              </div>
            ) : null}
          </Card>
        </div>

        <FilesCard project={project} />

        {config.length > 0 && <ConfigurationCard items={config} />}

        <Card>
          <CardHeader title={t("vehicle.hardware")} subtitle={t("vehicle.hardwareSubtitle")} />
          {!hw ? <div className="px-3 py-6 text-xs text-fg-muted">{t("vehicle.runFirst")}</div> : (
            <div className="grid grid-cols-1 xl:grid-cols-[360px_1fr]">
              <div className="border-r border-border">
                {KINDS.map((k) => {
                  const c = hw[k.key];
                  if (!c) return null;
                  return (
                    <button key={k.key} onClick={() => setSelected(k.key)} className={cn("flex w-full items-center gap-2 border-b border-border px-3 py-2 text-left text-xs hover:bg-panel-2", selected === k.key && "bg-calc/10")}>
                      <span className="w-24 shrink-0 text-fg-subtle">{t.tx(`vehicle.kind.${k.kind}`, k.kind)}</span>
                      <span className="flex-1 truncate font-medium">{c.name}</span>
                      {c.isOverride && <Badge tone="attn">{t("vehicle.overrideBadge")}</Badge>}
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
                        {spec.isOverride && <Button size="xs" variant="ghost" onClick={() => reset(kindOf.kind)}><RotateCcw className="size-3" />{t("common.reset")}</Button>}
                        <Button size="xs" onClick={() => setDialogKind(kindOf.kind)}><Pencil className="size-3" />{t("common.override")}</Button>
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

function ConfigurationCard({ items }: { items: ConfigurationItem[] }) {
  const t = useT();
  const groups = [...new Set(items.map((i) => i.group))];
  return (
    <Card>
      <CardHeader title={t("vehicle.configuration")} subtitle={t("vehicle.configurationSubtitle")} />
      <div className="grid grid-cols-1 gap-x-4 xl:grid-cols-2">
        {groups.map((g) => (
          <div key={g} className="border-b border-border">
            <div className="bg-panel-2/50 px-3 py-1 text-[10px] font-semibold uppercase tracking-wide text-fg-subtle">{t.tx(`vehicle.group.${g}`, g)}</div>
            <table className="w-full text-xs">
              <tbody>
                {items.filter((i) => i.group === g).map((i) => (
                  <tr key={i.key} className="border-t border-border/60 align-top [&>td]:px-3 [&>td]:py-1.5">
                    <td className="w-44 text-fg-subtle">{t.tx(`vehicle.item.${i.key}`, i.key)}</td>
                    <td>
                      <div className={cn("font-medium", !i.value && "text-unknown")}>{i.value ?? t("common.unknown")}</div>
                      {i.partNumber && <div className="num text-[11px] text-fg-muted">{t("vehicle.partNumber")}: {i.partNumber}</div>}
                      {i.alternatives.length > 0 && <div className="text-[11px] text-attn">{t("common.alternatives")}: {i.alternatives.map((a) => a.probability > 0 ? `${a.value} (${Math.round(a.probability * 100)}%)` : a.value).join(", ")}</div>}
                      {i.note && <div className="text-[11px] text-fg-muted">{i.note}</div>}
                      {i.evidence.length > 0 && <div className="truncate text-[11px] text-fg-subtle" title={i.evidence.join("; ")}>{i.evidence.join("; ")}</div>}
                    </td>
                    <td className="w-28 text-right">
                      <div className="flex flex-col items-end gap-1">
                        <Badge tone={ITEM_TONE[i.status]}>{t.tx(`vehicle.itemStatus.${i.status}`, i.status)}</Badge>
                        {i.status !== "Unknown" && <ConfidenceBadge score={i.confidence} showScore={false} />}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
      </div>
    </Card>
  );
}
