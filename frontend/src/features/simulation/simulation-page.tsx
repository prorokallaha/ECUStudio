"use client";
import type { AnalysisReport, Estimate } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Card, CardHeader, EstimateValue, SectionTitle } from "@/components/ui";
import { useWorkspace } from "@/hooks/use-workspace";
import { useSelection } from "@/stores/selection";
import { useT } from "@/i18n";

export function SimulationPage() {
  return <WithReport>{(r) => <Simulation r={r} />}</WithReport>;
}

function Simulation({ r }: { r: AnalysisReport }) {
  const t = useT();
  const { router, href } = useWorkspace();
  const select = useSelection((s) => s.select);
  const sim = r.simulation;
  return (
    <div>
      <PageHeader title={t("nav.simulation")} subtitle={t("simulation.subtitle", { n: sim.evaluatedPoints })} />
      <div className="space-y-4 p-4">
        <Card>
          <CardHeader title={t("simulation.scenarios")} subtitle={t("simulation.scenariosSubtitle")} />
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>{t("simulation.colScenario")}</th><th>{t("simulation.colPeakPower")}</th><th>{t("simulation.colPeakTorque")}</th><th>{t("simulation.colMaxEgt")}</th><th>{t("simulation.colMinLambda")}</th><th>{t("simulation.colMaxPr")}</th></tr></thead>
            <tbody>
              {sim.scenarios.map((s) => (
                <tr key={s.id} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5">
                  <td className="font-medium">{/^gear_\d+$/.test(s.id) ? t("simulation.scenarioGear", { g: s.id.slice(5) }) : t.tx(`simulation.scenario.${s.id}`, s.label)}</td><Td e={s.peakPowerHp} /><Td e={s.peakTorqueNm} /><Td e={s.maxEgtC} /><Td e={s.minLambda} /><Td e={s.maxPressureRatio} />
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <Card>
          <CardHeader title={t("simulation.fullLoad")} subtitle={t("simulation.fullLoadSubtitle")} />
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>{t("simulation.colRpm")}</th><th>{t("simulation.colTorque")}</th><th>{t("simulation.colPower")}</th><th>{t("simulation.colBoost")}</th><th>{t("simulation.colIq")}</th><th>λ</th><th>EGT</th><th>{t("simulation.colLimiters")}</th></tr></thead>
            <tbody>
              {sim.modifiedWot.map((w) => (
                <tr key={w.rpm} className="cursor-pointer border-t border-border hover:bg-panel-2 [&>td]:px-3 [&>td]:py-1" onClick={() => { select({ kind: "point", rpm: w.rpm, pedalPct: 100, gear: 4 }); router.push(href("dyno")); }}>
                  <td className="num">{w.rpm}</td><Td e={w.torqueNm} /><Td e={w.powerHp} /><Td e={w.boostMbar} /><Td e={w.iqMg} /><Td e={w.lambda} /><Td e={w.egtC} />
                  <td className="text-[11px] text-fg-muted">{[w.torqueLimiter, w.fuelLimiter, w.boostLimiter].filter((x) => x !== "None").map((x) => t.tx(`limiter.${x}`, x)).join(" · ") || "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <SectionTitle>{t("simulation.modelAssumptions")}</SectionTitle>
        <ul className="text-[11px] text-fg-muted">{(sim.assumptions ?? []).map((a, i) => <li key={i}>• {a}</li>)}</ul>
      </div>
    </div>
  );
}

function Td({ e }: { e: Estimate }) {
  return <td><EstimateValue e={e} size="sm" /></td>;
}
