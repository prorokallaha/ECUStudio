"use client";
import type { AnalysisReport, Estimate } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Card, CardHeader, EstimateValue, SectionTitle } from "@/components/ui";
import { useWorkspace } from "@/hooks/use-workspace";
import { useSelection } from "@/stores/selection";

export function SimulationPage() {
  return <WithReport>{(r) => <Simulation r={r} />}</WithReport>;
}

function Simulation({ r }: { r: AnalysisReport }) {
  const { router, href } = useWorkspace();
  const select = useSelection((s) => s.select);
  const sim = r.simulation;
  return (
    <div>
      <PageHeader title="Simulation" subtitle={`${sim.evaluatedPoints} operating points evaluated (adaptive grid, RPM × pedal × scenarios)`} />
      <div className="space-y-4 p-4">
        <Card>
          <CardHeader title="Scenarios" subtitle="ambient, altitude and coolant variations — worst case drives the risk verdict" />
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>Scenario</th><th>Peak power</th><th>Peak torque</th><th>Max EGT</th><th>Min λ</th><th>Max PR</th></tr></thead>
            <tbody>
              {sim.scenarios.map((s) => (
                <tr key={s.id} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5">
                  <td className="font-medium">{s.label}</td><Td e={s.peakPowerHp} /><Td e={s.peakTorqueNm} /><Td e={s.maxEgtC} /><Td e={s.minLambda} /><Td e={s.maxPressureRatio} />
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <Card>
          <CardHeader title="Full load (modified) — reference conditions" subtitle="click a row to inspect it on the Virtual Dyno" />
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>RPM</th><th>Torque</th><th>Power</th><th>Boost</th><th>IQ</th><th>λ</th><th>EGT</th><th>Active limiters</th></tr></thead>
            <tbody>
              {sim.modifiedWot.map((w) => (
                <tr key={w.rpm} className="cursor-pointer border-t border-border hover:bg-panel-2 [&>td]:px-3 [&>td]:py-1" onClick={() => { select({ kind: "point", rpm: w.rpm, pedalPct: 100, gear: 4 }); router.push(href("dyno")); }}>
                  <td className="num">{w.rpm}</td><Td e={w.torqueNm} /><Td e={w.powerHp} /><Td e={w.boostMbar} /><Td e={w.iqMg} /><Td e={w.lambda} /><Td e={w.egtC} />
                  <td className="text-[11px] text-fg-muted">{[w.torqueLimiter, w.fuelLimiter, w.boostLimiter].filter((x) => x !== "None").join(" · ") || "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <SectionTitle>Model assumptions</SectionTitle>
        <ul className="text-[11px] text-fg-muted">{(sim.assumptions ?? []).map((a, i) => <li key={i}>• {a}</li>)}</ul>
      </div>
    </div>
  );
}

function Td({ e }: { e: Estimate }) {
  return <td><EstimateValue e={e} size="sm" /></td>;
}
