"use client";
import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Crosshair } from "lucide-react";
import type { AnalysisReport, DynoRequest } from "@/types/domain";
import { api } from "@/services/api";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Card, CardHeader, EmptyState, EstimateValue, Input, SectionTitle, Select, Skeleton } from "@/components/ui";
import { DynoAuxChart, DynoMainChart } from "@/components/simulation/dyno-charts";
import { PointInspector } from "@/components/simulation/point-inspector";
import { useSelection } from "@/stores/selection";
import { useT } from "@/i18n";

export function DynoPage() {
  return <WithReport>{(r) => <Dyno r={r} />}</WithReport>;
}

const pickBoost = (p: any) => p.map, pickIq = (p: any) => p.iq, pickLambda = (p: any) => p.lambda, pickEgt = (p: any) => p.egt;

function Dyno({ r }: { r: AnalysisReport }) {
  const t = useT();
  const select = useSelection((s) => s.select);
  const [req, setReq] = useState<Partial<DynoRequest>>({ gear: 4, rpmStart: 1000, rpmEnd: 4800, rpmStep: 100, throttlePct: 100, ambientTempC: 20, altitudeM: 0 });
  const [debounced, setDebounced] = useState(req);
  useEffect(() => { const timer = setTimeout(() => setDebounced(req), 300); return () => clearTimeout(timer); }, [req]);
  const dyno = useQuery({ queryKey: ["dyno", r.id, debounced], queryFn: () => api.analyses.dyno(r.id, debounced), placeholderData: (prev) => prev });
  const inspect = useMutation({ mutationFn: (rpm: number) => api.analyses.inspect(r.id, { rpm, pedalPct: req.throttlePct ?? 100, gear: req.gear, ambientTempC: req.ambientTempC, altitudeM: req.altitudeM }) });
  const pick = (rpm?: number) => {
    if (!rpm) return;
    const n = Math.round(rpm / 50) * 50;
    inspect.mutate(n);
    select({ kind: "point", rpm: n, pedalPct: req.throttlePct ?? 100, gear: req.gear ?? 4, label: "Virtual Dyno" });
  };
  const num = (k: keyof DynoRequest) => (e: React.ChangeEvent<HTMLInputElement>) => setReq((s) => ({ ...s, [k]: Number(e.target.value) }));
  const d = dyno.data;
  const group = `dyno-${r.id}`;
  const smokeLabel = t("dyno.smokeRef");
  const smokeRef = useMemo(() => ({ value: 1.05, label: smokeLabel }), [smokeLabel]);

  return (
    <div className="flex h-full flex-col">
      <PageHeader title={t("nav.dyno")} subtitle={t("dyno.subtitle")} />
      <div className="flex flex-wrap items-end gap-3 border-b border-border px-4 py-2 text-xs">
        <Ctl label={t("dyno.gear")}><Select value={req.gear} onChange={(e) => setReq((s) => ({ ...s, gear: Number(e.target.value) }))}>{[1, 2, 3, 4, 5, 6].map((g) => <option key={g}>{g}</option>)}</Select></Ctl>
        <Ctl label={t("dyno.rpmFrom")}><Input type="number" className="w-20" value={req.rpmStart} step={100} onChange={num("rpmStart")} /></Ctl>
        <Ctl label={t("dyno.to")}><Input type="number" className="w-20" value={req.rpmEnd} step={100} onChange={num("rpmEnd")} /></Ctl>
        <Ctl label={t("dyno.step")}><Select value={req.rpmStep} onChange={(e) => setReq((s) => ({ ...s, rpmStep: Number(e.target.value) }))}>{[50, 100, 250].map((g) => <option key={g}>{g}</option>)}</Select></Ctl>
        <Ctl label={t("dyno.pedal")}><Input type="number" className="w-16" value={req.throttlePct} min={0} max={100} onChange={num("throttlePct")} /></Ctl>
        <Ctl label={t("dyno.ambient")}><Input type="number" className="w-16" value={req.ambientTempC} min={-20} max={45} onChange={num("ambientTempC")} /></Ctl>
        <Ctl label={t("dyno.altitude")}><Input type="number" className="w-20" value={req.altitudeM} min={0} max={3000} step={250} onChange={num("altitudeM")} /></Ctl>
        {d && (
          <div className="ml-auto flex gap-5">
            <Peak label={t("dyno.peakPower")} e={d.modified.peakPowerHp} rpm={d.modified.peakPowerRpm} stock={d.stock?.peakPowerHp} />
            <Peak label={t("dyno.peakTorque")} e={d.modified.peakTorque} rpm={d.modified.peakTorqueRpm} stock={d.stock?.peakTorque} />
          </div>
        )}
      </div>
      <div className="flex min-h-0 flex-1">
        <div className="flex min-w-0 flex-1 flex-col gap-2 overflow-y-auto p-3">
          {dyno.error && <EmptyState title={t("dyno.rejected")}>{String((dyno.error as Error).message)}</EmptyState>}
          {!d ? <Skeleton className="h-96" /> : (
            <>
              <Card className="h-[380px] shrink-0 p-2"><DynoMainChart d={d} onPick={pick} group={group} /></Card>
              <div className="grid shrink-0 grid-cols-2 gap-2">
                <Card className="h-44 p-1.5"><DynoAuxChart d={d} pick={pickBoost} title={t("dyno.auxBoost")} unit="mbar" group={group} onPick={pick} /></Card>
                <Card className="h-44 p-1.5"><DynoAuxChart d={d} pick={pickIq} title={t("dyno.auxIq")} unit="mg/stroke" group={group} onPick={pick} /></Card>
                <Card className="h-44 p-1.5"><DynoAuxChart d={d} pick={pickLambda} title={t("dyno.auxLambda")} unit="-" group={group} onPick={pick} refLine={smokeRef} /></Card>
                <Card className="h-44 p-1.5"><DynoAuxChart d={d} pick={pickEgt} title={t("dyno.auxEgt")} unit="°C" group={group} onPick={pick} /></Card>
              </div>
              <div className="text-[11px] text-fg-subtle">{d.disclaimer}</div>
              <SectionTitle>{t("dyno.assumptions")}</SectionTitle>
              <ul className="text-[11px] text-fg-muted">{d.assumptions.map((a, i) => <li key={i}>• {a}</li>)}</ul>
            </>
          )}
        </div>
        <aside className="w-[440px] shrink-0 overflow-y-auto border-l border-border bg-bg-elev">
          <Card className="m-2 border-0 bg-transparent">
            <CardHeader title={t("dyno.inspector")} icon={<Crosshair className="size-3.5" />} />
            <div className="p-3">
              {inspect.isPending && <Skeleton className="h-64" />}
              {inspect.data && !inspect.isPending && <PointInspector data={inspect.data} />}
              {!inspect.data && !inspect.isPending && <div className="text-xs text-fg-muted">{t("dyno.inspectorHint")}</div>}
            </div>
          </Card>
        </aside>
      </div>
    </div>
  );
}

function Ctl({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="space-y-0.5"><div className="text-[10px] uppercase tracking-wide text-fg-subtle">{label}</div>{children}</label>;
}

function Peak({ label, e, rpm, stock }: { label: string; e: any; rpm: number; stock?: any }) {
  const t = useT();
  return (
    <div>
      <div className="text-[10px] uppercase tracking-wide text-fg-subtle">{label}</div>
      <div className="flex items-baseline gap-1.5"><EstimateValue e={e} size="md" /><span className="num text-[11px] text-fg-subtle">@ {rpm}</span></div>
      {stock && <div className="text-[11px] text-fg-muted">{t("dyno.stock")} <EstimateValue e={stock} size="sm" compact /></div>}
    </div>
  );
}
