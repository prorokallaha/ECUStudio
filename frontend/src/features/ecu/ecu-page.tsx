"use client";
import type { AnalysisReport, Param } from "@/types/domain";
import { PageHeader, WithReport } from "@/components/layout/page";
import { Badge, Card, CardBody, CardHeader, ConfidenceBadge, SourceBadge } from "@/components/ui";
import { MemoryMap } from "@/components/binary/memory-map";
import { useWorkspace } from "@/hooks/use-workspace";
import { fmtBytes, fmtParam, hex } from "@/lib/format";

export function EcuPage() {
  return <WithReport>{(r) => <Ecu r={r} />}</WithReport>;
}

function Ecu({ r }: { r: AnalysisReport }) {
  const { router, href } = useWorkspace();
  const e = r.ecu;
  const rows: [string, Param | string][] = [
    ["Bosch number", e.boschNumber!], ["OEM part number", e.oemPartNumber!], ["Hardware number", e.hardwareNumber!], ["Software number", e.softwareNumber!],
    ["Software version", e.softwareVersion!], ["Engine code", e.engineCode!], ["Processor", e.processor ?? "Unknown"], ["Endianness", e.endianness ?? "?"],
    ["Flash size", fmtBytes(e.flashSize ?? 0)],
  ];
  return (
    <div>
      <PageHeader title="ECU" subtitle={`${e.ecuFamily} · plugin ${e.pluginId}`} />
      <div className="grid grid-cols-1 gap-4 p-4 xl:grid-cols-3">
        <Card>
          <CardHeader title="Identification" />
          <table className="w-full text-xs">
            <tbody>
              {rows.map(([k, v]) => (
                <tr key={k} className="border-b border-border last:border-0 [&>td]:px-3 [&>td]:py-1.5">
                  <td className="text-fg-subtle">{k}</td>
                  <td className="num">{typeof v === "string" ? v : <span className={fmtParam(v) === "UNKNOWN" ? "font-semibold text-unknown" : ""}>{fmtParam(v)}</span>}</td>
                  <td className="text-right">{typeof v !== "string" && v && fmtParam(v) !== "UNKNOWN" && <SourceBadge source={v.source} />}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <Card>
          <CardHeader title="Detection" subtitle={`score ${r.detection.score.toFixed(2)}`} />
          <CardBody className="space-y-1">
            {r.detection.reasons.map((x, i) => <div key={i} className="text-xs text-fg-muted">✓ {x}</div>)}
          </CardBody>
        </Card>
        <Card>
          <CardHeader title="Checksums" actions={<Badge tone={r.checksums.overall === "Valid" ? "ok" : r.checksums.overall === "Invalid" ? "danger" : "unknown"}>{r.checksums.overall}</Badge>} />
          <CardBody className="space-y-1.5 text-xs text-fg-muted">
            <div>{r.checksums.note}</div>
            {r.checksums.blocks.map((b, i) => <div key={i} className="num">{b.name}: {hex(b.start)}–{hex(b.end)} {b.status}</div>)}
          </CardBody>
        </Card>
        <Card className="xl:col-span-3">
          <CardHeader title="Memory map" subtitle="estimated sections; map addresses from definitions; changes vs stock" />
          <CardBody><MemoryMap r={r} onPick={(o) => router.push(href("binary", { offset: o }))} /></CardBody>
        </Card>
        <Card className="xl:col-span-3">
          <CardHeader title="Sections" />
          <table className="w-full text-xs">
            <thead className="text-[10px] uppercase tracking-wide text-fg-subtle"><tr className="[&>th]:px-3 [&>th]:py-1.5 [&>th]:text-left [&>th]:font-medium"><th>Name</th><th>Kind</th><th>Start</th><th>End</th><th>Size</th><th>Source</th><th>Confidence</th><th>Note</th></tr></thead>
            <tbody>
              {e.sections.map((s) => (
                <tr key={s.start} className="cursor-pointer border-t border-border hover:bg-panel-2 [&>td]:px-3 [&>td]:py-1.5" onClick={() => router.push(href("binary", { offset: s.start }))}>
                  <td>{s.name}</td><td>{s.kind}</td><td className="num">{hex(s.start)}</td><td className="num">{hex(s.end)}</td><td className="num">{fmtBytes(s.end - s.start)}</td>
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
