"use client";
import type { ComponentSpec } from "@/types/domain";
import { ConfidenceBadge, SourceBadge } from "@/components/ui";
import { fmtParam } from "@/lib/format";
import { CheckCircle2 } from "lucide-react";
import { useT } from "@/i18n";

/** Every KB parameter shows value, unit, source, confidence and whether a person verified it. Labels: `params.<key>` in the dictionaries. */
export function ParamTable({ spec }: { spec: ComponentSpec }) {
  const t = useT();
  const entries = Object.entries(spec.parameters ?? {});
  if (!entries.length) return <div className="px-3 py-4 text-xs text-fg-muted">{t("components.noParams")}</div>;
  return (
    <table className="w-full text-xs">
      <thead className="text-[10px] uppercase tracking-wide text-fg-subtle">
        <tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>{t("components.colParameter")}</th><th>{t("common.value")}</th><th>{t("common.source")}</th><th>{t("common.confidence")}</th><th>{t("components.colVerified")}</th></tr>
      </thead>
      <tbody>
        {entries.map(([k, p]) => {
          const unknown = fmtParam(p) === "UNKNOWN";
          return (
            <tr key={k} className="border-t border-border [&>td]:px-3 [&>td]:py-1.5" title={p.note ?? undefined}>
              <td className="text-fg-muted">{t.tx(`params.${k}`, k)}</td>
              <td className={unknown ? "font-semibold text-unknown" : "num"}>{t.val(fmtParam(p))}</td>
              <td><SourceBadge source={p.source} /></td>
              <td>{unknown ? <span className="text-[10px] text-fg-subtle">—</span> : <ConfidenceBadge score={p.confidence} />}</td>
              <td>{p.userVerified ? <CheckCircle2 className="size-3.5 text-ok" /> : <span className="text-fg-subtle">{t("components.notVerified")}</span>}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
