"use client";
import type { PointInspection, PointResult } from "@/types/domain";
import { Badge, ConfidenceBadge, EstimateValue, SectionTitle } from "@/components/ui";
import Link from "next/link";
import { useWorkspace } from "@/hooks/use-workspace";
import { useT, type TKey } from "@/i18n";

const ROWS: (keyof PointResult)[] = [
  "requestedTorque", "permittedTorque", "iqRequested", "iq", "boostTarget", "map", "pressureRatio", "compressorOutletTemp",
  "intakeManifoldTemp", "airMass", "airFlow", "lambda", "soi", "duration", "railPressure", "egt", "torque", "powerHp",
];

/** Operating Point Inspector: every intermediate quantity with the formula, the map it came from and the ensemble breakdown. */
export function PointInspector({ data }: { data: PointInspection }) {
  const m = data.modified;
  const s = data.stock;
  const { href } = useWorkspace();
  const t = useT();
  return (
    <div className="space-y-3 text-xs">
      <div className="flex flex-wrap items-center gap-2">
        <span className="num text-sm font-semibold">{t("dyno.point", { rpm: m.point.rpm, pedal: m.point.pedalPct, gear: m.point.gear })}</span>
        <Badge tone="calc">{t("dyno.limTorque", { v: t.tx(`limiter.${m.torqueLimiter}`, m.torqueLimiter) })}</Badge>
        <Badge tone="calc">{t("dyno.limFuel", { v: t.tx(`limiter.${m.fuelLimiter}`, m.fuelLimiter) })}</Badge>
        <Badge tone="calc">{t("dyno.limBoost", { v: t.tx(`limiter.${m.boostLimiter}`, m.boostLimiter) })}</Badge>
        {m.beyondCalibratedRange && <Badge tone="warn">{t("dyno.beyond")}</Badge>}
      </div>
      <table className="w-full">
        <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:py-1 [&>th]:text-left [&>th]:font-medium"><th>{t("dyno.colQuantity")}</th><th>{t("dyno.colModified")}</th>{s && <th>{t("dyno.colStock")}</th>}</tr></thead>
        <tbody>
          {ROWS.map((k) => (
            <tr key={k} className="border-t border-border [&>td]:py-1">
              <td className="text-fg-muted">{t(`dyno.row.${k}` as TKey)}</td>
              <td><EstimateValue e={m[k] as any} size="sm" compact /></td>
              {s && <td><EstimateValue e={s[k] as any} size="sm" compact className="opacity-70" /></td>}
            </tr>
          ))}
        </tbody>
      </table>
      {!!m.models?.length && (
        <div>
          <SectionTitle className="mb-1">{t("dyno.ensemble")}</SectionTitle>
          <table className="w-full">
            <tbody>
              {m.models.map((x) => (
                <tr key={x.model} className="border-t border-border [&>td]:py-1" title={x.description}>
                  <td className="font-medium">{x.model}</td>
                  <td className="text-fg-muted">{x.description}</td>
                  <td className="num text-right">{x.available ? `${x.torqueNm.toFixed(0)} (${x.low.toFixed(0)}–${x.high.toFixed(0)}) Nm` : <span className="text-unknown">{t("dyno.na")}</span>}</td>
                  <td className="text-right">{x.available && <ConfidenceBadge score={x.confidence} showScore={false} />}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="mt-1 text-[11px] text-fg-subtle">{t("dyno.disagreement")}</div>
        </div>
      )}
      {!!m.trace?.length && (
        <div>
          <SectionTitle className="mb-1">{t("dyno.trace")}</SectionTitle>
          <ol className="space-y-1">
            {m.trace.map((step, i) => (
              <li key={i} className="rounded border border-border bg-bg px-2 py-1">
                <div className="flex items-baseline justify-between gap-2">
                  <span className="font-medium">{step.quantity}</span>
                  <span className="num">{Number.isInteger(step.value) ? step.value : step.value.toFixed(3)} {step.unit === "-" ? "" : step.unit}</span>
                </div>
                <div className="font-mono text-[10px] text-fg-subtle">{step.formula}</div>
                {(step.mapId || step.note) && <div className="text-[10px]">{step.mapId && <Link className="text-calc hover:underline" href={href(`maps/${step.mapId}`)}>{step.mapId}</Link>} <span className="text-fg-muted">{step.note}</span></div>}
              </li>
            ))}
          </ol>
        </div>
      )}
    </div>
  );
}
