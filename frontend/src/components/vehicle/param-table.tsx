import type { ComponentSpec } from "@/types/domain";
import { ConfidenceBadge, SourceBadge } from "@/components/ui";
import { fmtParam } from "@/lib/format";
import { CheckCircle2 } from "lucide-react";

const LABELS: Record<string, string> = {
  displacement_cc: "Displacement", cylinders: "Cylinders", bore_mm: "Bore", stroke_mm: "Stroke", compression_ratio: "Compression ratio",
  rated_power_kw: "Rated power", rated_power_rpm: "Rated power rpm", rated_torque_nm: "Rated torque", rated_torque_rpm: "Rated torque rpm",
  max_design_torque_nm: "Max design torque", injection_system: "Injection system", max_pressure_ratio: "Max pressure ratio",
  max_corrected_flow_kg_s: "Max corrected flow", max_turbine_inlet_temp_c: "Max turbine inlet temp", max_speed_rpm: "Max shaft speed",
  max_delivery_mg: "Max delivery", max_injection_duration_deg_ca: "Max injection duration", max_injection_pressure_bar: "Max injection pressure",
  max_rail_pressure_bar: "Max rail pressure", rated_input_torque_nm: "Rated input torque", clutch_capacity_nm: "Clutch capacity",
};

/** Every KB parameter shows value, unit, source, confidence and whether a person verified it. */
export function ParamTable({ spec }: { spec: ComponentSpec }) {
  const entries = Object.entries(spec.parameters ?? {});
  if (!entries.length) return <div className="px-3 py-4 text-xs text-fg-muted">No parameters in the knowledge base for this component.</div>;
  return (
    <table className="w-full text-xs">
      <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
        <tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>Parameter</th><th>Value</th><th>Source</th><th>Confidence</th><th>Verified</th></tr>
      </thead>
      <tbody>
        {entries.map(([k, p]) => {
          const unknown = fmtParam(p) === "UNKNOWN";
          return (
            <tr key={k} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5" title={p.note ?? undefined}>
              <td className="text-fg-muted">{LABELS[k] ?? k}</td>
              <td className={unknown ? "font-semibold text-unknown" : "num"}>{fmtParam(p)}</td>
              <td><SourceBadge source={p.source} /></td>
              <td>{unknown ? <span className="text-[10px] text-fg-subtle">—</span> : <ConfidenceBadge score={p.confidence} />}</td>
              <td>{p.userVerified ? <CheckCircle2 className="size-3.5 text-ok" /> : <span className="text-fg-subtle">no</span>}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
