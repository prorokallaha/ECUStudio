"use client";
import Link from "next/link";
import type { AnalysisReport, ComponentSpec } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Badge, Card, CardHeader, ConfidenceBadge, SeverityBadge, SourceBadge } from "@/components/ui";
import { ParamTable } from "@/components/vehicle/param-table";
import { useWorkspace } from "@/hooks/use-workspace";
import { useT } from "@/i18n";

export function ComponentsPage() {
  return <WithReport>{(r) => <Components r={r} />}</WithReport>;
}

function Components({ r }: { r: AnalysisReport }) {
  const t = useT();
  const { href } = useWorkspace();
  const hw = r.vehicle.profile.hardware as unknown as Record<string, ComponentSpec>;
  const keys = ["engine", "turbo", "injectors", "fuelSystem", "transmission", "clutch", "intercooler", "sensors", "emissions"];
  const marginFor = (k: string) => r.risk.components.find((c) => c.component === (k === "fuelSystem" ? "fuel_system" : k));
  return (
    <div>
      <PageHeader title={t("nav.components")} subtitle={t("components.subtitle")} actions={<Link href={href("vehicle")} className="text-xs text-calc hover:underline">{t("components.overrideLink")}</Link>} />
      <div className="grid grid-cols-1 gap-3 p-4 xl:grid-cols-2">
        {keys.map((k) => {
          const c = hw[k];
          if (!c) return null;
          const m = marginFor(k);
          return (
            <Card key={k}>
              <CardHeader title={`${t.tx(`vehicle.kind.${c.kind}`, c.kind)} · ${c.name}`} actions={<>{c.isOverride && <Badge tone="attn">{t("vehicle.overrideBadge")}</Badge>}<SourceBadge source={c.source} /><ConfidenceBadge score={c.confidence} />{m && <SeverityBadge severity={m.severity} />}</>} />
              <ParamTable spec={c} />
            </Card>
          );
        })}
      </div>
    </div>
  );
}
