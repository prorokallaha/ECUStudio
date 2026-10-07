"use client";
import type { PointInspection, PointResult } from "@/types/domain";
import { Badge, ConfidenceBadge, EstimateValue, SectionTitle } from "@/components/ui";
import Link from "next/link";
import { useWorkspace } from "@/hooks/use-workspace";

/** Operating Point Inspector: every intermediate quantity with the formula, the map it came from and the ensemble breakdown. */
export function PointInspector({ data }: { data: PointInspection }) {
  const m = data.modified;
  const s = data.stock;
  const { href } = useWorkspace();
  const rows: [string, keyof PointResult][] = [
    ["Requested torque", "requestedTorque"], ["Permitted torque", "permittedTorque"], ["IQ requested", "iqRequested"], ["IQ", "iq"],
    ["Boost target", "boostTarget"], ["Manifold pressure", "map"], ["Pressure ratio", "pressureRatio"], ["Compressor out T", "compressorOutletTemp"],
    ["Intake manifold T", "intakeManifoldTemp"], ["Air mass", "airMass"], ["Air flow", "airFlow"], ["Lambda", "lambda"], ["SOI", "soi"],
    ["Duration", "duration"], ["Rail pressure", "railPressure"], ["EGT", "egt"], ["Torque", "torque"], ["Power", "powerHp"],
  ];
  return (
    <div className="space-y-3 text-xs">
      <div className="flex flex-wrap items-center gap-2">
        <span className="num text-sm font-semibold">{m.point.rpm} rpm · {m.point.pedalPct}% · gear {m.point.gear}</span>
        <Badge tone="calc">torque: {m.torqueLimiter}</Badge>
        <Badge tone="calc">fuel: {m.fuelLimiter}</Badge>
        <Badge tone="calc">boost: {m.boostLimiter}</Badge>
        {m.beyondCalibratedRange && <Badge tone="warn">beyond calibrated range</Badge>}
      </div>
      <table className="w-full">
        <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:py-1 [&>th]:text-left [&>th]:font-medium"><th>Quantity</th><th>Modified</th>{s && <th>Stock</th>}</tr></thead>
        <tbody>
          {rows.map(([label, k]) => (
            <tr key={k} className="border-t border-border [&>td]:py-1">
              <td className="text-fg-muted">{label}</td>
              <td><EstimateValue e={m[k] as any} size="sm" compact /></td>
              {s && <td><EstimateValue e={s[k] as any} size="sm" compact className="opacity-70" /></td>}
            </tr>
          ))}
        </tbody>
      </table>
      {!!m.models?.length && (
        <div>
          <SectionTitle className="mb-1">Torque models (ensemble)</SectionTitle>
          <table className="w-full">
            <tbody>
              {m.models.map((x) => (
                <tr key={x.model} className="border-t border-border [&>td]:py-1" title={x.description}>
                  <td className="font-medium">{x.model}</td>
                  <td className="text-fg-muted">{x.description}</td>
                  <td className="num text-right">{x.available ? `${x.torqueNm.toFixed(0)} (${x.low.toFixed(0)}–${x.high.toFixed(0)}) Nm` : <span className="text-unknown">n/a</span>}</td>
                  <td className="text-right">{x.available && <ConfidenceBadge score={x.confidence} showScore={false} />}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="mt-1 text-[11px] text-fg-subtle">Disagreement between models widens the final range.</div>
        </div>
      )}
      {!!m.trace?.length && (
        <div>
          <SectionTitle className="mb-1">Calculation trace</SectionTitle>
          <ol className="space-y-1">
            {m.trace.map((t, i) => (
              <li key={i} className="rounded border border-border bg-bg px-2 py-1">
                <div className="flex items-baseline justify-between gap-2">
                  <span className="font-medium">{t.quantity}</span>
                  <span className="num">{Number.isInteger(t.value) ? t.value : t.value.toFixed(3)} {t.unit === "-" ? "" : t.unit}</span>
                </div>
                <div className="font-mono text-[10px] text-fg-subtle">{t.formula}</div>
                {(t.mapId || t.note) && <div className="text-[10px]">{t.mapId && <Link className="text-calc hover:underline" href={href(`maps/${t.mapId}`)}>{t.mapId}</Link>} <span className="text-fg-muted">{t.note}</span></div>}
              </li>
            ))}
          </ol>
        </div>
      )}
    </div>
  );
}
