"use client";
import type { AnalysisReport, Param } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Badge, Card, CardBody, CardHeader, ConfidenceBadge, SourceBadge } from "@/components/ui";
import { MemoryMap } from "@/components/binary/memory-map";
import { useWorkspace } from "@/hooks/use-workspace";
import { fmtBytes, fmtParam, hex } from "@/lib/format";
import { DefinitionBindingCard, DefinitionMatchesCard } from "@/features/library/definition-cards";
import { useT } from "@/i18n";

export function EcuPage() {
  return <WithReport>{(r) => <Ecu r={r} />}</WithReport>;
}

function Ecu({ r }: { r: AnalysisReport }) {
  const t = useT();
  const { router, href, projectId, analysisId } = useWorkspace();
  const e = r.ecu;
  const rows: [string, Param | string][] = [
    [t("ecu.boschNumber"), e.boschNumber!], [t("ecu.oemPartNumber"), e.oemPartNumber!], [t("ecu.oemHardwarePartNumber"), e.oemHardwarePartNumber!], [t("ecu.projectCode"), e.projectCode!], [t("ecu.hardwareNumber"), e.hardwareNumber!], [t("ecu.softwareNumber"), e.softwareNumber!],
    [t("ecu.softwareVersion"), e.softwareVersion!], [t("vehicle.engineCode"), e.engineCode!], [t("ecu.processor"), e.processor ?? t("ecu.unknownProcessor")],
    [t("ecu.endianness"), e.endianness ? t.tx(`endian.${e.endianness}`, e.endianness) : "?"], [t("ecu.flashSize"), fmtBytes(e.flashSize ?? 0)],
  ];
  return (
    <div>
      <PageHeader title={t("nav.ecu")} subtitle={t("ecu.subtitle", { family: e.ecuFamily, plugin: e.pluginId })} />
      <div className="grid grid-cols-1 gap-4 p-4 xl:grid-cols-3">
        <Card>
          <CardHeader title={t("vehicle.identification")} />
          <table className="w-full text-xs">
            <tbody>
              {rows.map(([k, v]) => (
                <tr key={k} className="border-b border-border last:border-0 [&>td]:px-3 [&>td]:py-1.5">
                  <td className="text-fg-subtle">{k}</td>
                  <td className="num">{typeof v === "string" ? v : <span className={fmtParam(v) === "UNKNOWN" ? "font-semibold text-unknown" : ""}>{t.val(fmtParam(v))}</span>}</td>
                  <td className="text-right">{typeof v !== "string" && v && fmtParam(v) !== "UNKNOWN" && <SourceBadge source={v.source} />}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <Card>
          <CardHeader title={t("ecu.detection")} subtitle={t("ecu.score", { s: r.detection.score.toFixed(2) })} />
          <CardBody className="space-y-1">
            {r.detection.reasons.map((x, i) => <div key={i} className="text-xs text-fg-muted">✓ {x}</div>)}
          </CardBody>
        </Card>
        <Card>
          <CardHeader title={t("ecu.checksums")} actions={<Badge tone={r.checksums.overall === "Valid" ? "ok" : r.checksums.overall === "Invalid" ? "danger" : "unknown"}>{t.tx(`checksum.${r.checksums.overall}`, r.checksums.overall)}</Badge>} />
          <CardBody className="space-y-1.5 text-xs text-fg-muted">
            <div>{r.checksums.note}</div>
            {r.checksums.blocks.map((b, i) => (
              <div key={i} className="rounded border border-border px-2 py-1.5">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium text-fg">{b.name}</span>
                  <Badge tone={b.status === "Valid" ? "ok" : b.status === "Invalid" ? "danger" : "unknown"}>{t.tx(`checksum.${b.status}`, b.status)}</Badge>
                </div>
                <div className="num">{b.algorithm}</div>
                {b.stored && <div className="num">{t("ecu.stored", { s: b.stored, c: b.computed })}</div>}
                {!b.stored && b.computed && <div>{b.computed}</div>}
              </div>
            ))}
          </CardBody>
        </Card>
        {projectId && <DefinitionBindingCard r={r} projectId={projectId} />}
        {projectId && analysisId && <DefinitionMatchesCard analysisId={analysisId} projectId={projectId} className="xl:col-span-2" />}
        <Card className="xl:col-span-3">
          <CardHeader title={t("ecu.memoryMap")} subtitle={t("ecu.memoryMapSubtitle")} />
          <CardBody><MemoryMap r={r} onPick={(o) => router.push(href("binary", { offset: o }))} /></CardBody>
        </Card>
        <Card className="xl:col-span-3">
          <CardHeader title={t("ecu.sections")} />
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>{t("ecu.colName")}</th><th>{t("ecu.colKind")}</th><th>{t("ecu.colStart")}</th><th>{t("ecu.colEnd")}</th><th>{t("ecu.colSize")}</th><th>{t("common.source")}</th><th>{t("common.confidence")}</th><th>{t("ecu.colNote")}</th></tr></thead>
            <tbody>
              {e.sections.map((s) => (
                <tr key={s.start} className="cursor-pointer border-t border-border hover:bg-panel-2 [&>td]:px-3 [&>td]:py-1.5" onClick={() => router.push(href("binary", { offset: s.start }))}>
                  <td>{s.name}</td><td>{t.tx(`sectionKind.${s.kind}`, s.kind)}</td><td className="num">{hex(s.start)}</td><td className="num">{hex(s.end)}</td><td className="num">{fmtBytes(s.end - s.start)}</td>
                  <td><SourceBadge source={s.source} /></td><td><ConfidenceBadge score={s.confidence} /></td><td className="text-fg-muted">{s.note}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      </div>
    </div>
  );
}
